# RUMI Printer-App — Agent Rules

> Auto-loaded by Claude Code on every session in this repository. These rules apply to ALL code changes in `printer-app/`.
> First read on a cold session: this file → [docs/SPRINT-PLAN.md](docs/SPRINT-PLAN.md) (refactoring track) + the sprint task you're picking up.

---

## §1 — Identity

- **Stack**: .NET MAUI 10 (multi-target: `net10.0-android;net10.0-windows10.0.19041.0` — the Windows TFM is OS-conditioned so non-Windows hosts build Android only), C# 13, ESC/POS thermal-printer driver. See [ADR-005](docs/adr/ADR-005-multi-target-maui-android.md) (Phase 1 of the cross-platform plan).
- **Runtime**: Windows 10+ (existing rollout) **and** Android 7+ / API 24 (new, primary rollout). iOS/macOS/Tizen scaffolding is present but unbuilt (iOS deferred to v2). **Android does not print yet** — the network transport lands in Phase 2; Phase 1 only makes Android compile + launch.
- **Build caveat**: `*-windows` TFMs build only on Windows. On macOS/Linux/CI-Linux, `dotnet build` produces the Android artifact only — the Windows MSI + any MAUI-10 regression must be verified on a Windows host before release.
- **Architecture**: Service-oriented MVVM with code-behind (standard MAUI pattern), DI registration in `MauiProgram.cs`
- **Hosted on**: GitHub — https://github.com/piwas-21/restaurant-app-printer
- **Production**: distributed to client workstations as a packaged Windows app via the GitHub releases-based `UpdateService`
- **In-flight workspace**: this repo is one of three under [/Users/mahmutkaya/workspace/rumi-workspace/](../). The workspace meta-repo holds cross-repo plans and the master roadmap. When this repo is cloned standalone, only this `CLAUDE.md` is in scope.

## §2 — Critical files to read

| When | Read |
|---|---|
| Any task | This file |
| Refactoring sprint task | [docs/SPRINT-PLAN.md](docs/SPRINT-PLAN.md) — find the task ID, read its acceptance criteria |
| Quality/security gate work | [docs/QUALITY-SECURITY-PLAN.md](docs/QUALITY-SECURITY-PLAN.md) |
| Test work | [docs/TEST-COVERAGE-PLAN.md](docs/TEST-COVERAGE-PLAN.md) |
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
│   ├── IPrinterService.cs / WindowsPrinterService.cs           # Windows Printer API (P/Invoke)
│   ├── IOrderPrintService.cs / OrderPrintService.cs            # ESC/POS formatting, receipt composition
│   ├── IOrderHistoryService.cs / OrderHistoryService.cs        # Persisted order history + dedup window
│   ├── PrinterType.cs                                          # Kitchen / Cashier discriminator
│   ├── PrintStyleSettingsService.cs                            # Style settings persistence
│   ├── IRequestLogService.cs / RequestLogService.cs            # Request/response logging
│   └── IUpdateService.cs / UpdateService.cs                    # GitHub release auto-update
├── Converters/                            # XAML value converters
├── Pages/                                 # Additional pages
├── Platforms/                             # Platform-specific code (Windows only)
├── Properties/, Resources/                # MAUI scaffolding (themes, images)
└── PrinterAPP.csproj
```

### Key patterns

- **Service-oriented with dependency injection** via `MauiProgram.cs`. Every service registered as `AddSingleton<IFoo, Foo>()`.
- **MVVM with code-behind**: standard MAUI pattern. View binds to code-behind directly; pure ViewModel layer is intentionally absent at this scale.
- **Intelligent printer routing**: orders dispatch to Cashier, Front Kitchen, or Back Kitchen printers based on item category.
- **5-second SSE polling** with a 1-hour deduplication window so re-emitted orders during reconnects don't double-print.
- **ESC/POS** thermal printer commands with Turkish character support (codepage PC857) — see `OrderPrintService.cs`.
- **Auto-update** via GitHub releases: `UpdateService` polls latest release on startup, downloads installer if newer, prompts user.

### Backend API contract

The printer-app is a **strict consumer** of the backend API. Models in `PrinterAPP/Models/` MUST mirror backend DTO field names and types exactly — silent drift breaks order printing in production.

- Auth: `X-Api-Key` header on the printer-feed endpoint (since printer-app !1 / backend MR !20). Legacy `Authorization: Bearer <jwt>` flow is removed.
- Backend base URL is configured per-deployment in `PrinterConfiguration.cs` and persisted in the user's local config JSON.

---

## §4 — File length limits

Enforced (blocking) by `scripts/check-file-length.sh` (pre-commit + CI) and warned in-loop by the PostToolUse checker. Max LOC: **`.xaml.cs` code-behind 200 · `Services/` 300 · `Models/` 80 · `Platforms/Windows/` P-Invoke 200 · `*Constants.cs` 100 · `Converters/` 60**. Over the limit ⇒ move logic to a service (code-behind only wires UI), one service = one concern. Excludes non-Windows platform shims (`Platforms/{Android,iOS,MacCatalyst,Tizen}`). Existing violations baselined in `scripts/file-length-baseline.txt`; opt out with `// FILE_LENGTH_EXEMPT: <reason>` (first 5 lines); after a refactor drops a file under limit run `bash scripts/check-file-length.sh --regen-baseline` and commit the baseline.

---

## §5 — Printer-app rules (hard)

1. **All services have interfaces.** Register via `MauiProgram.cs` (`builder.Services.AddSingleton<IFoo, Foo>()`). Naming: `I{Feature}Service.cs` + `{Feature}Service.cs` (or `Windows{Feature}Service.cs` for platform-specific implementations). New services MUST follow this; four legacy services (`RequestLogService`, `OrderPrintService`, `OrderHistoryService`, `UpdateService`) are tracked for retrofit in [#3](https://github.com/piwas-21/restaurant-app-printer/issues/3) — until then, do not add new code that depends on them concretely; wait for the interface.
2. **Code-behind contains only UI event handlers.** Business logic, state mutations, and I/O live in services. If a `.xaml.cs` exceeds 200 LOC, that's a sign you're putting logic in the wrong layer.
3. **Models must mirror backend DTOs exactly.** Field names, types, casing, and nullability must match `backend/RestaurantSystem.Api/Features/<X>/Dtos/`. Before changing a model, grep the corresponding backend DTO and confirm — silent drift is a production-printing failure.
4. **ESC/POS commands defined in a constants file**, not inline. Magic byte sequences in print code are a debugging tarpit.
5. **No hardcoded backend URLs / API keys / printer names** in source. Everything user-configurable goes through `PrinterConfiguration` (persisted as `config.json`).
6. **No hardcoded paths** (`C:\...`) — use `FileSystem.AppDataDirectory` or platform-appropriate APIs. The app must work for users with non-default Windows install locations.
7. **Async methods suffix with `Async`.** `await` everything; never `.Result` / `.Wait()` (deadlock on UI thread).
8. **PascalCase** for public members, `_camelCase` for private fields, `UPPER_SNAKE` is not used (avoid C-style).
9. **No `null!` on model fields** — use `required` modifier or `= string.Empty` / sensible defaults. Nullable reference types are enabled per project (`<Nullable>enable</Nullable>`).
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

- **Pre-commit** (blocking): trailing-ws / EOF / large-files / secret-scan / no-commit-to-protected; file-length (§4); `dotnet build PrinterAPP.sln`.
- **CI**: `dotnet build`, Gitleaks, CodeQL. `dotnet format` + a test suite are planned (Sprint 2/3). ⚠️ MAUI Windows-target builds need a Windows runner, so the CI build is `allow_failure: true` today — `build-windows.{sh,ps1}` is the pre-merge source of truth.
- **Weekly** `security-audit.yml` (cron): OSV full-tree, Trivy fs (HIGH/CRITICAL), gitleaks full-history, `dotnet list package --vulnerable` — fails red on findings.
- **New-dev setup**: `pwsh -File scripts/setup_hooks.ps1` (Windows) or `bash scripts/setup_hooks.sh` (macOS/Linux — hooks only; build needs Windows).

---

## §8 — Git workflow

### Branch strategy

```
main                    ← production releases (tagged, auto-update consumes these)
  └── develop           ← integration / pre-release branch
       ├── feature/<x>
       ├── fix/<x>
       ├── chore/<x>
       └── docs/<x>
```

- **Never push to `main` or `develop` directly** — pre-commit hook blocks this.
- Branch off **`develop`**. Open PR to `develop`. After merge to `develop` and validation, `develop` is promoted to `main` for a release.
- Default branch on remote: `develop`.
- One issue = one branch. Delete branch after merge (`--remove-source-branch`).
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
2. Read [docs/SPRINT-PLAN.md](docs/SPRINT-PLAN.md) if picking up a sprint task.
3. On Windows: run `dotnet build PrinterAPP.sln` — confirm baseline green.
4. On macOS / Linux: limited to non-build edits (or use `dotnet build` against a different TFM for syntax-only validation).
5. Check `git status` — start from clean tree on `develop`.

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
