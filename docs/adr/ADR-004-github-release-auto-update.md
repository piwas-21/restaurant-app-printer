# ADR-004 — GitHub-release-based auto-update

**Status:** Accepted
**Date:** 2026-04-29
**Author:** mahmutkaya
**Reviewers:** —
**Implements / supersedes:** Initial release pipeline
**References:**
- [PrinterAPP/Services/UpdateService.cs](../../PrinterAPP/Services/UpdateService.cs) — release polling + downloader
- [PrinterAPP/UpdaterWindow.xaml.cs](../../PrinterAPP/UpdaterWindow.xaml.cs) — user-facing update prompt
- [RELEASE_WORKFLOW.md](../../RELEASE_WORKFLOW.md) — release process

---

## Context

The printer-app is installed on the cashier and kitchen workstations of restaurants. Users running the app are not technical: they don't open admin consoles, they don't run installers, and they don't know what version they're on.

When we ship a fix or a feature we need every workstation on the new build within hours, not weeks. Manually shipping installers (USB stick, support-call walk-through) doesn't scale past a handful of installations.

The product is a small standalone Windows app, not part of the Microsoft Store ecosystem. Building a custom backend service for app distribution would be more infrastructure than the value justifies. We host source on GitLab, but the **release artifacts** can live anywhere with a stable URL and a SemVer manifest.

GitHub Releases provide:
- Versioned, downloadable assets
- A simple JSON API to query "what's the latest?"
- Tag-based addressability so a rollback is a tag re-point

## Decision

**The printer-app self-updates by polling a public GitHub repository's releases endpoint on startup (and on a recurring timer), comparing the latest tag to the installed version, and prompting the user to install the new MSI if newer.**

The flow:
1. On launch, `UpdateService` queries `https://api.github.com/repos/<org>/<repo>/releases/latest`.
2. Compares the response's `tag_name` (parsed as SemVer) to the installed version (`AppInfo.VersionString`).
3. If newer, downloads the MSI asset to a temp folder.
4. Shows `UpdaterWindow` with release notes from the GitHub release body. User clicks "Install" → `UpdateService` runs the MSI and exits the app.
5. Windows Installer replaces the binaries; the new version launches on next start.

The GitHub repo identity (`<org>/<repo>` and the public-asset URL) is **part of the application identity** — changing it strands every existing install (CLAUDE.md §9 hard refusal).

## Consequences

### Positive
- **Zero infrastructure** — GitHub Releases is free, durable, and globally CDN'd.
- **Update channel is auditable** — every release is a tag, every artifact is a downloadable URL with a hash.
- **Rollback is a tag re-point** — re-publishing the previous version as `latest` causes the next polling cycle to "downgrade" workstations.
- **Release notes carry through** — what the maintainer writes in the GitHub release body is what the cashier sees in the update prompt.

### Negative
- **GitHub becomes a runtime dependency** — if `api.github.com` is unreachable, the auto-update flow degrades silently (the app still starts and prints; it just doesn't know about updates).
- **The update channel is public.** Anyone can download the MSI. Code-signing the installer (separate decision) is what prevents tampered substitution; not the privacy of the URL.
- **No staged rollouts.** "Latest" is global. Pushing a release breaks every installation simultaneously if the release is broken. We rely on testing on `develop` before a `main` cutover (tracked in CLAUDE.md §8).

### Mitigation for the negatives
- The check is best-effort: failure is logged via `RequestLogService` and does not block app startup or order printing.
- All releases must be code-signed before publishing; users see a Windows SmartScreen warning otherwise.
- For staged rollouts, future work can add a `release_channel` field to `PrinterConfiguration` (`stable` / `beta`) and have `UpdateService` query `releases?per_page=10` filtered by tag prefix. Not in scope today.

## Alternatives considered

### Alternative A: ClickOnce
Standard .NET / Windows mechanism for self-updating desktop apps. Works, but constrains the installer format and adds Windows-specific deployment metadata that the team has no operational experience with. Rolling our own polling against GitHub releases is ~150 LOC and gives us full control over the prompt UX.

### Alternative B: A self-hosted update server (Squirrel.Windows / Velopack)
Battle-tested OSS update frameworks. More features (delta updates, staged rollouts, channel support). Adds a self-hosted server or per-customer update endpoint. Overkill for the current scale.

### Alternative C: Microsoft Store distribution
Solves signing, distribution, and updates in one. Trades against: the customer's IT setup may not allow Store apps; the app uses Win32 P/Invoke (`winspool.drv`) and would need certification; the review process adds latency to fixes. The customer's cashier workstations are managed by us, not the customer's IT — Store distribution would be a regression in deploy speed.
