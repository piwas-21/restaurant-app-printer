# RUMI Printer-App — Agent Rules

> Auto-loaded by Claude Code on every session in this repository. These rules apply to ALL code changes in `printer-app/`.
> First read on a cold session: this file → [docs/SPRINT-PLAN.md](docs/SPRINT-PLAN.md) (refactoring track) + the sprint task you're picking up.

---

## §1 — Identity

- **Stack**: .NET MAUI 9 (Windows-only target: `net9.0-windows10.0.19041.0`), C# 12, ESC/POS thermal-printer driver
- **Runtime**: Windows 10+ (build target restricts to Windows; iOS/Android/macOS targets are not built)
- **Architecture**: Service-oriented MVVM with code-behind (standard MAUI pattern), DI registration in `MauiProgram.cs`
- **Hosted on**: GitLab — https://gitlab.com/restaurant-app3282120/printer-app
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
│   ├── IPrinterService.cs / WindowsPrinterService.cs          # Windows Printer API (P/Invoke)
│   ├── OrderPrintService.cs                                    # ESC/POS formatting, receipt composition
│   ├── OrderHistoryService.cs                                  # Persisted order history + dedup window
│   ├── PrintStyleSettingsService.cs                            # Style settings persistence
│   ├── RequestLogService.cs                                    # Request/response logging
│   └── UpdateService.cs                                        # GitHub release auto-update
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

Enforced by reviewer; will be enforced by `scripts/check-quality.sh` once Sprint 2 lands.

| File type | Max LOC | Action if exceeded |
|---|---|---|
| Page code-behind (`.xaml.cs`) | 200 | Move logic into a service; code-behind should only wire UI events |
| Service class | 300 | Split by concern — one service = one responsibility |
| Model class | 80 | Data containers only; no behaviour |
| P/Invoke wrapper | 200 | Isolate native interop in a dedicated wrapper |
| Constants file (ESC/POS commands, config keys) | 100 | Group by category and split |
| Converter (XAML `IValueConverter`) | 60 | One converter = one transformation |

Known exceptions are documented inline in each file with a comment block (`// FILE_LENGTH_EXEMPT: <reason>`).

---

## §5 — Printer-app rules (hard)

1. **All services have interfaces.** Register via `MauiProgram.cs` (`builder.Services.AddSingleton<IFoo, Foo>()`). Naming: `I{Feature}Service.cs` + `{Feature}Service.cs` (or `Windows{Feature}Service.cs` for platform-specific implementations).
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

## §7 — Quality gates

| Gate | When | What | Blocking? | Source of truth |
|---|---|---|---|---|
| Pre-commit hooks | Every `git commit` | trailing whitespace, EOF, large files, secret scan, no-commit-to-protected | yes | [.pre-commit-config.yaml](.pre-commit-config.yaml) |
| `dotnet build PrinterAPP.sln` | Pre-commit (when `.cs/.csproj/.sln/.xaml` staged) **and** MR pipeline | 0 errors | yes | `.gitlab-ci.yml` (see note below) |
| Gitleaks | MR pipeline | No leaked credentials (allowlist via `.gitleaks.toml`) | yes | [.gitleaks.toml](.gitleaks.toml) |
| GitLab SAST | MR pipeline | Auto-injected analyzers | yes | `.gitlab-ci.yml` |
| `dotnet format --verify-no-changes` | Sprint 2 (planned) | 0 formatting drift | future | (not yet wired) |
| Test suite | Sprint 3 (planned) | Unit + integration tests | future | [docs/TEST-COVERAGE-PLAN.md](docs/TEST-COVERAGE-PLAN.md) |
| Trivy / dependency scan | Sprint 4 (planned) | NuGet supply-chain scan | future | (not yet wired) |

> **Build runner caveat**: MAUI Windows-targeting builds need a Windows runner. The default GitLab.com shared runners are Linux; the MAUI workload `dotnet build` will fail on Linux for the `windows10.0.19041` target framework. Sprint 2 wires a self-hosted Windows runner; until then, the CI build job runs on best-effort and is `allow_failure: true`. Local builds via `build-windows.sh` (Git Bash) or `build-windows.ps1` are the source of truth pre-merge.

### Setup for a new developer
```powershell
# Windows / PowerShell
pwsh -File scripts/setup_hooks.ps1   # installs pre-commit hooks (one-time)
.\build-windows.ps1                   # local build
```
```bash
# macOS / Linux (hooks only — actual build requires Windows)
bash scripts/setup_hooks.sh           # installs pre-commit hooks (one-time)
```

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
- Branch off **`develop`**. Open MR to `develop`. After merge to `develop` and validation, `develop` is promoted to `main` for a release.
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

Every MR uses [.gitlab/merge_request_templates/Default.md](.gitlab/merge_request_templates/Default.md). Required sections:
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
- **`PrinterAPP.csproj` `<TargetFramework>`** — changing the Windows TFM (currently `net9.0-windows10.0.19041.0`) is an architecture decision (need a new ADR). Patch-level SDK bumps via `global.json` are fine.
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
5. Open MR via `glab mr create` (or GitLab UI) — fill in the template fully, including acceptance-criteria coverage table.
