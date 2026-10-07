# RUMI Printer-App — Agent Rules

> Auto-loaded by Claude Code on every session in this repository. These rules apply to ALL code changes in `printer-app/`.
> First read on a cold session: this file + the GitHub issue you're picking up. Cross-repo state lives in the
> workspace [ROADMAP.md](../ROADMAP.md).

---

## §1 — Identity

- **Stack**: .NET MAUI 10 (multi-target: `net10.0-android;net10.0-windows10.0.19041.0` — the Windows TFM is OS-conditioned so non-Windows hosts build Android only), C# 13, ESC/POS thermal-printer driver. See [ADR-005](docs/adr/ADR-005-multi-target-maui-android.md) (Phase 1 of the cross-platform plan).
- **Runtime**: Windows 10+ (existing rollout) **and** Android 7+ / API 24, `targetSdkVersion` 36 (new, primary rollout). iOS/macOS/Tizen scaffolding is present but unbuilt (iOS deferred to v2). Android prints over network TCP via `IPrinterTransport` ([ADR-006](docs/adr/ADR-006-printer-transport-abstraction.md)) and keeps running off-screen via a foreground service ([ADR-007](docs/adr/ADR-007-android-foreground-service.md)); the Windows spooler path is unchanged.
- **Build caveat**: `*-windows` TFMs build only on Windows. On macOS/Linux/CI-Linux, `dotnet build` produces the Android artifact only — the Windows MSI + any MAUI-10 regression must be verified on a Windows host before release.
- **Architecture**: Service-oriented MVVM with code-behind (standard MAUI pattern), DI registration in `MauiProgram.cs`
- **Hosted on**: GitHub — https://github.com/piwas-21/restaurant-app-printer
- **Production**: distributed to client workstations as a packaged Windows app via the GitHub releases-based `UpdateService`
- **In-flight workspace**: this repo is one of three under [/Users/mahmutkaya/workspace/rumi-workspace/](../). The workspace meta-repo holds cross-repo plans and the master roadmap. When this repo is cloned standalone, only this `CLAUDE.md` is in scope.

## §2 — Critical files to read

| When | Read |
|---|---|
| Any task | This file |
| Quality/security gate work | §7 below (current gates) + the workspace [DEV-PHASES-PLAN.md](../docs/plans/DEV-PHASES-PLAN.md) §2 coverage matrix |
| Test work | [docs/DEVELOPMENT-GUIDELINES.md](docs/DEVELOPMENT-GUIDELINES.md) §Testing |
| Security review / threat model | [docs/SECURITY-AUDIT.md](docs/SECURITY-AUDIT.md) |
| Coding conventions detail | [docs/DEVELOPMENT-GUIDELINES.md](docs/DEVELOPMENT-GUIDELINES.md) |
| Architectural decisions | [docs/adr/README.md](docs/adr/README.md) — index of ADRs |
| Starting a session | Run `dotnet build PrinterAPP.sln` (Windows) or `bash build-windows.sh` to establish baseline |
| Bug fix / feature | Read the relevant ADR if one exists for the affected subsystem |

---

## §3 — Architecture

### Code layout

```
PrinterAPP/
├── App.xaml(.cs), AppShell.xaml(.cs)     # MAUI app entry + shell
├── MauiProgram.cs                         # DI registration (service + interface bindings)
├── MainPage.xaml(.cs)                     # Primary UI + settings
├── OrderManagementPage.xaml(.cs)          # Order history + manual print
├── LogsPage.xaml(.cs)                     # Request log viewer
├── ErrorLogsPage.xaml(.cs), WarningLogsPage.xaml(.cs)
├── UpdaterWindow.xaml(.cs)                # Update flow UI
├── Models/
│   ├── Order.cs                           # Mirrors backend OrderDto exactly
│   ├── PrinterConfiguration.cs            # Saved settings (JSON)
│   ├── PrintStyleSettings.cs              # Font sizes, bold, emphasis
│   └── UpdateInfo.cs                      # GitHub release metadata
├── Services/
│   ├── IEventStreamingService.cs / EventStreamingService.cs   # SSE polling, order event handling
│   ├── IOrderPipeline.cs / OrderPipeline.cs                    # Headless feed→print→history→ack path (ADR-007)
│   ├── IBackgroundRunner.cs / DirectBackgroundRunner.cs        # Platform seam for off-screen execution
│   ├── IFeedCursorStore.cs / FeedCursorStore.cs                # Persisted poll cursor + dedup set (no reprint on restart)
│   ├── IFeedWatchdog.cs / FeedWatchdog.cs / FeedWatchdogDecision.cs  # Restarts a dead or stalled feed
│   ├── IPrinterService.cs / WindowsPrinterService.cs           # Windows Printer API (P/Invoke)
│   ├── IOrderPrintService.cs / OrderPrintService.cs            # ESC/POS formatting, receipt composition
│   ├── ReceiptComposer.cs                                      # Shared per-item block (ingredients, components, notes) both surfaces compose through
│   ├── PrintLabels.cs / PrintLanguagePolicy.cs                 # Receipt label catalog (en/de/fr/it/es/nl/tr) + how the venue's print-language setting resolves
│   ├── IOrderHistoryService.cs / OrderHistoryService.cs        # In-memory order history (last 100) + dedup window
│   ├── PrinterType.cs                                          # Kitchen / Cashier discriminator
│   ├── PrintStyleSettingsService.cs                            # Style settings persistence
│   ├── IRequestLogService.cs / RequestLogService.cs            # Request/response logging
│   └── IUpdateService.cs / UpdateService.cs                    # GitHub release auto-update
├── Converters/                            # XAML value converters
├── Pages/                                 # Additional pages
├── Platforms/
│   ├── Windows/                           # P/Invoke + WinUI app head
│   └── Android/                           # App head + OrderFeedForegroundService, BootReceiver (ADR-007)
├── Properties/, Resources/                # MAUI scaffolding (themes, images)
└── PrinterAPP.csproj
```

### Key patterns

- **Service-oriented with dependency injection** via `MauiProgram.cs`. Every service registered as `AddSingleton<IFoo, Foo>()`.
- **MVVM with code-behind**: keep lightweight screens directly bound where that stays clear; use a ViewModel when a screen owns stateful history, projection, or command workflows. Keep code-behind to UI events and dialogs.
- **Intelligent printer routing**: orders dispatch to Cashier, Front Kitchen, or Back Kitchen printers based on each line's `KitchenType`. Since backend PR #237 made `OrderDto.Items` **root-only**, a bundle's components live only in `OrderItem.SideItems`, nested to arbitrary depth — so routing walks the whole tree (`KitchenTicketFilter`, a pure function like `FeedWatchdogDecision`), never just the top level. A component whose kitchen differs from its parent's goes to *its own* kitchen's ticket, which carries the parent line as context. **Anything that reasons about "the order's items" must recurse** — a top-level-only scan silently prints no ticket at all for that kitchen.
- **5-second SSE polling** — the first poll fires immediately on start (a restarted Android foreground service must not leave the pass blind), every one after it 5 s apart; the interval is a constructor parameter defaulting to 5 s so tests can drive the loop without paying its wall clock — with a 1-hour deduplication window so re-emitted orders during reconnects don't double-print. The poll cursor and dedup set are **persisted** (`IFeedCursorStore`), so a restart resumes instead of re-printing the last 30 minutes. A dedup entry is persisted only once the print path confirms the order (`ConfirmOrderHandled`) — a kill between dispatch and print must re-drive the order, never silently suppress it. Three timings are coupled and must stay ordered — `unconfirmed retention (25 min) < cursor look-back clamp (30 min) < dedup window (1 h)`. The clamp must stay under the dedup window or a re-fetch reaches orders no surviving dedup entry guards (reprints); unconfirmed retention must stay under the clamp or an unconfirmed order's cursor floor is discarded by the very load it exists to influence (silently dropped ticket). An unconfirmed order that ages out is reported to the Errors page — past that point it genuinely cannot be recovered.
- **Feed watchdog** (`IFeedWatchdog`): restarts a feed that died or stopped completing polls. It must never override a deliberate stop — see `FeedWatchdogDecision`, where all of that logic lives as a pure, tested function.
- **Headless order pipeline** (`IOrderPipeline`): the feed→print→history→ack path is owned by a service, not a page, so it runs with no Activity. On Android an `OrderFeedForegroundService` hosts it (see [ADR-007](docs/adr/ADR-007-android-foreground-service.md)); on Windows it runs in-process. **Never move print or feed logic back into a page** — that is what made the app stop printing whenever it was minimised.
- **ESC/POS** thermal printer commands with Turkish character support (codepage PC857) — see `OrderPrintService.cs`.
- **Auto-update** via GitHub releases: `UpdateService` polls latest release on startup, downloads installer if newer, prompts user.

### Backend API contract

The printer-app is a **strict consumer** of the backend API. Models in `PrinterAPP/Models/` MUST mirror backend DTO field names and types exactly — silent drift breaks order printing in production.

- **`OrderDto.Items` is ROOT-ONLY** (backend PR #237 / issue #234, merged 2026-07-27). Bundle components and add-on sides are NOT top-level entries — they hang off their parent in `OrderItemDto.SideItems`, to arbitrary depth, each with its own `KitchenType`. No field changed, so drift here does not fail deserialization; it fails silently at the till.
- Auth: `X-Api-Key` header on the printer-feed endpoint (since printer-app !1 / backend MR !20). Legacy `Authorization: Bearer <jwt>` flow is removed.
- Backend base URL is configured per-deployment in `PrinterConfiguration.cs` and persisted in the user's local config JSON.

---

## §4 — File length limits

Enforced (blocking) by `scripts/check-file-length.sh` (pre-commit + CI) and warned in-loop by the PostToolUse checker. Max LOC: **`.xaml.cs` code-behind 200 · `Services/` 300 · `Models/` 80 · `Platforms/Windows/` P-Invoke 200 · `*Constants.cs` 100 · `Converters/` 60**. Over the limit ⇒ move logic to a service (code-behind only wires UI), one service = one concern. Excludes non-Windows platform shims (`Platforms/{Android,iOS,MacCatalyst,Tizen}`). Existing violations baselined in `scripts/file-length-baseline.txt`; opt out with `// FILE_LENGTH_EXEMPT: <reason>` (first 5 lines); after a refactor drops a file under limit run `bash scripts/check-file-length.sh --regen-baseline` and commit the baseline.

---

## §5 — Printer-app rules (hard)

1. **All services have interfaces.** Register via `MauiProgram.cs` (`builder.Services.AddSingleton<IFoo, Foo>()`). Naming: `I{Feature}Service.cs` + `{Feature}Service.cs` (or `{Platform}{Feature}Service.cs` / `{Platform}{Feature}Runner.cs` for platform-specific implementations — e.g. `WindowsPrinterService`, `AndroidBackgroundRunner`). Collaborators that are not "services" in the CRUD sense may drop the suffix where it reads better (`IOrderPipeline`, `IBackgroundRunner`); the interface + DI-registration requirement still applies. New services MUST follow this; four legacy services (`RequestLogService`, `OrderPrintService`, `OrderHistoryService`, `UpdateService`) are tracked for retrofit in [#3](https://github.com/piwas-21/restaurant-app-printer/issues/3) — until then, do not add new code that depends on them concretely; wait for the interface.
2. **Code-behind contains only UI event handlers.** Business logic, state mutations, and I/O live in services. If a `.xaml.cs` exceeds 200 LOC, that's a sign you're putting logic in the wrong layer.
3. **Models must mirror backend DTOs exactly.** Field names, types, casing, and nullability must match `backend/RestaurantSystem.Api/Features/<X>/Dtos/`. Before changing a model, grep the corresponding backend DTO and confirm — silent drift is a production-printing failure.
4. **ESC/POS commands defined in a constants file**, not inline. Magic byte sequences in print code are a debugging tarpit.
5. **No hardcoded backend URLs / API keys / printer names** in source. Everything user-configurable goes through `PrinterConfiguration` (persisted as `config.json`).
6. **No hardcoded paths** (`C:\...`) — use `FileSystem.AppDataDirectory` or platform-appropriate APIs. The app must work for users with non-default Windows install locations.
7. **Async methods suffix with `Async`.** `await` everything; never `.Result` / `.Wait()` (deadlock on UI thread).
8. **PascalCase** for public members, `_camelCase` for private fields, `UPPER_SNAKE` is not used (avoid C-style).
9. **No `null!` on model fields** — use `required` modifier or `= string.Empty` / sensible defaults. Nullable reference types are enabled solution-wide via the root `Directory.Build.props` (`<Nullable>enable</Nullable>`, since DEV-PHASES W1). The MAUI app head carries pre-existing CS86xx nullable-warning debt in the HttpClient/JSON/P-Invoke service paths — burndown owed; don't add new nullable warnings.
10. **Logging**: use `RequestLogService` for HTTP I/O, `WarningLogsPage` / `ErrorLogsPage` for user-visible error surfaces. No `Console.WriteLine` / `Debug.WriteLine` in production paths.

---

## §6 — Pre-implementation verification (REQUIRED for non-trivial work)

> Output this checklist BEFORE writing any implementation code. Skipping = restart the task.
> "Non-trivial" = anything beyond a one-line typo / comment fix.

### 1. Backend contract verification (any change touching `Models/` or HTTP client code)
For each model field referenced, name the source of truth:
- Backend DTO path: `backend/RestaurantSystem.Api/Features/<X>/Dtos/<Y>Dto.cs`
- Field name + type as it appears there
- For nested objects, confirm both sides match exactly.

### 2. Sibling conventions
List 2–3 sibling files in the directory you're adding to. Note their structure (DI registration, interface naming, base class). Confirm your new file matches.

### 3. Acceptance criteria audit
Quote the relevant criteria from the sprint task / issue. Mark each:
- **Covered fully** (this PR closes it)
- **Partial** (note what's missing, link follow-up)
- **Out of scope** (note where it'll land)

### 4. Existing references
Grep for the type/method/key you're adding or modifying. List every callsite. Confirm each still works after your change OR mark for update in this PR.

### 5. Platform / build check
- Does this change touch Platforms/Windows/ or P/Invoke?
- Will the change require a release-engineering update (signing, installer, GitHub release notes)?
- If yes, flag in the MR description.

---

## §7 — Quality gates (source of truth `.github/workflows/ci.yml` + `.pre-commit-config.yaml`)

- **Pre-commit** (blocking): trailing-ws / EOF / YAML-JSON-XML checks / large-files / secret-scan (detect-secrets) / no-commit-to-protected; file-length (§4). No build gate in pre-commit — `dotnet build PrinterAPP.sln` is a manual pre-merge step on Windows.
- **CI** (`ci.yml`), seven jobs: `dotnet_test` runs the plain .NET tests and compiles the source-linked E2E project;
  `maui_compile` builds the Android app head on Ubuntu; `maui_windows_compile` builds the Windows app head on
  Windows (isolating its TFM as the release workflow does); `file_length`, `gitleaks`, `trufflehog` and
  `trivy_fs` publish the four scan names required by the branch rules. Each scan runs independently.
  Both app-head builds use Debug and require no signing secrets. Windows device/runtime checks and
  installer validation remain separate; `dotnet format` and CodeQL are still pending (#4).
- **Weekly** `security-audit.yml` (cron): OSV full-tree, Trivy fs (HIGH/CRITICAL), gitleaks full-history, `dotnet list package --vulnerable` — fails red on findings.
- **New-dev setup**: `pwsh -File scripts/setup_hooks.ps1` (Windows) or `bash scripts/setup_hooks.sh` (macOS/Linux — hooks only; build needs Windows).

### Not enforced yet (planned gates — do not lose these)

Carried over from the deleted GitLab-era `docs/QUALITY-SECURITY-PLAN.md` (2026-08-17); everything else in
that doc is either shipped above or GitLab-only. Cross-repo status lives in the workspace
[DEV-PHASES-PLAN.md](../docs/plans/DEV-PHASES-PLAN.md) §2.

| Gate | Status / blocker |
|---|---|
| `dotnet format --verify-no-changes` + XAML format (XamlStyler) | needs a Windows runner for the MAUI workload; same constraint as CodeQL ([#4](https://github.com/piwas-21/restaurant-app-printer/issues/4)) |
| Roslyn analyzers (SonarAnalyzer/SecurityCodeScan) + `TreatWarningsAsErrors` | deferred in `Directory.Build.props` until the CS86xx nullable debt burns down |
| SAST / SonarCloud quality gate | ACTIVE on this repo — a SonarCloud project exists and PRs are analysed; the merge gate's Sonar step (quality gate + zero open delta issues) is enforced, not a no-op |
| Coverage floor on `PrinterAPP.Tests` | tests run in CI but no minimum is enforced, and no target has been agreed |
| Automated **DTO-drift check** vs `backend/.../Features/**/Dtos/` | §5.3 / §6.1 are enforced by review only; the cross-repo diff script was specced and never built — the highest-value missing gate for this repo (silent drift = no ticket at the till) |
| Release supply chain: Authenticode-sign the **Windows** artifact, publish `SHA256SUMS` + SBOM, verify the last release's signature | `build-release.yml` signs the **Android** APK only; the unsigned/unhashed Windows exe is the other half of SECURITY-AUDIT C1 (client-side update verification has nothing to verify against) |
| Dependency hygiene extras: `dotnet list package --outdated`, license audit (block GPL/AGPL transitives) | weekly `security-audit.yml` covers CVEs + secrets only |

---

## §8 — Git workflow

### Branch strategy (GitFlow — updated 2026-07-10; supersedes the retired 2026-06-30 main-based model)

```
develop                 ← DEFAULT + integration branch; all feature work targets it
  ├── feature/<x>       → PR to develop
  ├── fix/<x>           → PR to develop
  ├── chore/<x>         → PR to develop
  └── docs/<x>          → PR to develop

main                    ← production RELEASES ONLY; updated solely via a develop→main release PR
```

- **Never push directly to `main` or `develop`.** Enforcement here is **local only**, unlike the other
  app repos: this repo is **private on a free org plan**, where GitHub offers neither rulesets nor
  branch protection (`GET /repos/.../rulesets` → *403 "Upgrade to GitHub Pro or make this repository
  public"*; `GET /repos/.../branches/{main,develop}` → `"protected": false`, verified 2026-08-16). The
  earlier claim that a no-bypass `main-develop` Ruleset blocked pushes server-side was **wrong for this
  repo**. What actually stands between a mistake and `develop` is the pre-commit `no-commit-to-branch`
  hook, the push-time review gate, and the merge gate — all of them local, all of them bypassable by
  anyone who chooses to. Treat that as a reason for MORE care, not less.
- **Branch off `develop`; open every `feature/`·`fix/`·`chore/`·`docs/`·`test/` PR to `develop`.**
  Merge only via `scripts/pr-merge-gate.sh piwas-21/restaurant-app-printer <pr> --merge`, which
  requires **every** CI check green (by state, not by name), zero unresolved review threads and zero
  open Sonar issues. Since nothing is required server-side, that script is the gate.
- **Releases:** open a PR **`develop` → `main`**, then tag `v*` on `main` → `build-release.yml` publishes the Windows exe + Android APK to the public releases repo.
- One issue = one branch. Delete branch after merge (`gh pr merge --delete-branch`).
- Branch naming: `feature/`, `fix/`, `chore/`, `docs/`, `test/`.

### Commit messages

Format: `type(scope): description`

| Type | Use for |
|---|---|
| `feat` | New feature visible to user |
| `fix` | Bug fix |
| `refactor` | Code change with no behaviour change |
| `chore` | Build / CI / dependencies / config |
| `docs` | Documentation only |
| `test` | Tests only |
| `perf` | Performance improvement |

Body should explain **why**, not what (the diff shows what).

### Merge requests

Every PR uses [.github/pull_request_template.md](.github/pull_request_template.md). Required sections:
- Summary
- Sprint task / issue link
- Acceptance criteria coverage table
- Backend contract verification (for `Models/` or HTTP client changes)
- Standard checklist (build, lint, no hardcoded secrets, sibling conventions matched)

---

## §9 — AI guardrails (refusal list)

Never auto-edit these files / take these actions without explicit user instruction:

### Hard refusals
- **`config.json` shipped to a customer machine.** That file is per-installation user state, not source. The repo's `PrinterConfiguration` defaults are the source of truth for new installs.
- **`Platforms/Windows/Package.appxmanifest`** identity / signing fields — these tie to the code-signing certificate and the Windows Store / sideload identity. Changes are a release-engineering event.
- **`PrinterAPP.csproj` `<TargetFrameworks>`** — the project multi-targets `net10.0-android;net10.0-windows10.0.19041.0` (Windows TFM OS-conditioned) per [ADR-005](docs/adr/ADR-005-multi-target-maui-android.md). **Adding/removing a TFM (e.g. iOS in v2) or changing the .NET major is an architecture decision — needs a new ADR.** Patch-level SDK bumps via `global.json` are fine.
- **`UpdateService.cs` release URL / GitHub repo identity** — that's the auto-update channel. Changing it strands every existing install.
- **Branch protection bypass**: never `git commit --no-verify`, `git push --force-with-lease` to `develop`/`main`, `git reset --hard` on `develop`/`main`.

### Cross-repo coordination required
- **`Models/<X>.cs`** field renames or removals — these mirror backend DTOs. Before changing, grep `backend/RestaurantSystem.Api/Features/*/Dtos/` for the corresponding type and flag the cross-repo impact in the MR.
- **HTTP client auth changes** — affects backend printer-feed endpoint config. Coordinate the deploy.

### Sensitive-file refusal (matches gitleaks/detect-secrets allowlist)
Never commit:
- `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.cer`, `*.snk`, `*.keystore`, `*.jks`
- Code-signing certs of any kind
- `config.json` from a real installation (contains backend URL + API key)
- `.env*`

---

## §10 — Session workflow

### Starting
1. Read this file (auto-loaded).
2. On Windows: run `dotnet build PrinterAPP.sln` — confirm baseline green.
3. On macOS / Linux: limited to non-build edits (or use `dotnet build` against a different TFM for syntax-only validation).
4. Check `git status` — start from clean tree on `develop`.

### During implementation
1. Output the §6 verification block before writing code.
2. After each file change, the agent's PostToolUse hook (Sprint 2) will warn on file-length / forbidden-pattern violations.
3. Run `dotnet build` after non-trivial changes — catches type errors early.
4. Use `PrinterConfiguration` for any user-configurable value, never hardcoded literals.
5. Use `RequestLogService` for HTTP logging, never `Console.WriteLine`.

### Before ending
1. `dotnet build PrinterAPP.sln` → 0 errors (Windows).
2. `git status` → only intentional changes staged.
3. Commit with `type(scope):` format.
4. Push to feature branch.
5. Open PR via `gh pr create` (or GitHub UI) — fill in the template fully, including acceptance-criteria coverage table.
