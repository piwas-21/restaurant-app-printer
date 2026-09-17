# How to Create a Release for Printer App (Automated)

## Distribution architecture (read once)

The **source** repo (`piwas-21/restaurant-app-printer`) is **private**. Private-repo release assets
require a logged-in GitHub account with repo access, so the client can't download them and the in-app
updater (which calls the GitHub API unauthenticated) gets a 404.

To fix this, binaries are published to a separate **public** repo:
**[`piwas-21/printer-app-releases`](https://github.com/piwas-21/printer-app-releases)**.

- `build-release.yml` uploads the `.exe`/`.apk` there (`repository:` + `token:` on the release step).
- `UpdateService.cs` (`GITHUB_REPO` const) reads latest release from there — no auth needed.
- The link you share with the client is a stable public URL from that repo's Releases page.

### One-time setup: `RELEASES_REPO_TOKEN` secret

Cross-repo upload needs a Personal Access Token (the default `GITHUB_TOKEN` only reaches the repo the
workflow runs in). Create a **fine-grained PAT** scoped to **only** `piwas-21/printer-app-releases`:

1. GitHub → Settings → Developer settings → Fine-grained tokens → **Generate new token**.
2. **Resource owner**: `piwas-21`. **Repository access**: Only select repositories → `printer-app-releases`.
3. **Permissions**: Repository permissions → **Contents: Read and write**.
4. Copy the token, then add it as a secret on the **source** repo:
   ```bash
   gh secret set RELEASES_REPO_TOKEN -R piwas-21/restaurant-app-printer
   ```
5. Set an expiry reminder — when the PAT expires, releases fail to upload until it's regenerated.

> **Migrating existing installs** — two separate cut-offs, one per platform:
>
> | Platform | Auto-update works from | Older installs |
> |---|---|---|
> | Windows | **1.0.16** | Poll the old updater URL; can't reach the fix. |
> | Android | **1.0.20** | Auto-update support did not exist. |
>
> Android builds before 1.0.20 had no `.apk` branch in `UpdateService`, so they evaluated the Windows
> arm — and since `Environment.Is64BitOperatingSystem` is true on arm64, "Update Now" downloaded
> `PrinterApp-Setup-x64.exe` (~300 MB) and failed in the batch-script install path with *"Update
> failed. Please try again or download manually."* They also had no startup update prompt.
>
> Either way the device must download the new build **once** from the public Releases page; auto-update
> works for every release after that. The APK is signed with the same keystore across releases
> (`ANDROID_KEYSTORE_BASE64`), so it installs **in place** — no uninstall, and `config.json`, the API
> key and printer settings survive. Verify before telling a client to sideload:
>
> ```bash
> apksigner verify --print-certs PrinterApp-Android.apk
> ```
>
> If the signer certificate differs from the build already on the device, the install fails with a
> signature mismatch and the operator must uninstall first — losing their configuration.

## Step 1: Update Version Number

Edit `PrinterAPP/PrinterAPP.csproj` and increment all three version values:

```xml
<!-- `<Version>` becomes the assembly version that UpdateService compares with the release tag. -->
<Version>1.0.30</Version>
<!-- Android's user-visible version label. -->
<ApplicationDisplayVersion>1.0.30</ApplicationDisplayVersion>
<!-- Android's integer versionCode; it must be greater than the previous APK's code. -->
<ApplicationVersion>29</ApplicationVersion>
```

`ApplicationDisplayVersion` alone does not fix an updater that is built with an older assembly
version, and reusing an Android `ApplicationVersion` prevents an APK from installing over the
previous build. Keep both values increasing alongside `<Version>` for every release.

## Step 2: Commit and Tag

GitHub Actions (`build-release.yml`) builds **both platforms** on a version tag and attaches them to
the release:
- **Windows** (`build-windows` matrix, one leg per RID, then the `release-windows` job) —
  `PrinterApp-Setup-x64.exe` / `-x86.exe` (consumed by the in-app auto-updater). The legs build in
  parallel and only upload artifacts; `release-windows` attaches both files in one call, so the two
  legs cannot race to create the same release. If one arch fails, the other is still released, and a
  release with **no** installer is refused outright — the auto-updater would otherwise see a newer
  version it cannot download.
- **Android** (`build-android` job) — `PrinterApp-Android.apk` (sideload).

1. Commit your changes:
   ```bash
   git add .
   git commit -m "chore: bump version to 1.0.3"
   git push
   ```

2. Create and push a tag:
   ```bash
   git tag v1.0.3
   git push origin v1.0.3
   ```

## Step 3: Wait for Build

1. Go to your GitHub repository -> **Actions** tab.
2. You will see a "Build and Release" workflow running.
3. Wait ~5 minutes for it to complete.

## Step 4: Publish Release

1. Go to **Releases**.
2. You will see a new release created automatically (or a draft).
3. The files `PrinterApp-Setup-x64.exe` and `PrinterApp-Setup-x86.exe` will be attached automatically.
4. Edit the release to add release notes if desired.

## Step 5: Restaurant Can Now Update

**Windows users:**
1. Open Printer App
2. Click "🔄 Update" button
3. Click "Check for Updates"
4. If update available, click "Update Now"
5. App automatically downloads correct version (x64 or x86), installs, and restarts

**Android users (sideload):**
1. On the tablet, download `PrinterApp-Android.apk` from the GitHub Release page.
2. Allow "Install unknown apps" for the browser/file manager (Android settings) the first time.
3. Open the APK and install. (No Play Store auto-update yet — that's a future Phase 5 item.)

## Android signing (one-time setup)

The `build-android` job signs the APK with a release keystore read from GitHub Actions secrets.
**Until these secrets are set, the job builds a Debug-signed APK** — installable for testing, but
**not** for production distribution (debug key + debuggable build).

1. Generate a keystore (keep it safe and backed up — losing it means you can't ship updates that
   install over an existing install):
   ```bash
   keytool -genkeypair -v -keystore printerapp.keystore -alias printerapp \
     -keyalg RSA -keysize 2048 -validity 10000
   ```
2. In **repo Settings -> Secrets and variables -> Actions**, add:
   - `ANDROID_KEYSTORE_BASE64` — `base64 -i printerapp.keystore` (the whole file, base64-encoded)
   - `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` (e.g. `printerapp`), `ANDROID_KEY_PASSWORD`
3. Re-tag (or re-run the workflow); the Android job now produces a signed Release APK.

> Play Store managed distribution + in-app updates are a later Phase 5 add-on; sideload covers the
> initial Android rollout.

## Version Numbering

- **Major.Minor.Patch** format (e.g., 1.0.1)
- **Patch** (0.0.X): Bug fixes, small improvements
- **Minor** (0.X.0): New features, non-breaking changes
- **Major** (X.0.0): Breaking changes, major rewrites

## Troubleshooting

**Q: Build failed on GitHub Actions**
A: Check the Actions logs. Ensure code compiles locally.

**Q: Tag pushed but no release created**
A: Check `.github/workflows/build-release.yml` syntax or permissions.

**Q: App update fails**
A: Ensure the generated `PrinterApp-Setup.exe` is roughly 60MB+ in size (it includes .NET runtime).
