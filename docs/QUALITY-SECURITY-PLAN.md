# Printer-App — Quality & Security Hardening Plan

Stack: **.NET MAUI 9**, Windows-only, ESC/POS thermal-printer driver. Hosted on GitLab.

> Read [/QUALITY-SECURITY-PLAN.md](../../docs/plans/QUALITY-SECURITY-PLAN.md) first. This document only adds the MAUI/Windows-specific tasks.

---

## 0. Current state

- **No `.gitlab-ci.yml`** — zero CI today
- **No test project** — `PrinterAPP.sln` ships only the app project
- **No pre-commit hooks**
- Build is manual via [build-windows.ps1](../build-windows.ps1) / [build-windows.sh](../build-windows.sh) per [README-BUILD.md](../README-BUILD.md)
- Release flow described in [RELEASE_WORKFLOW.md](../RELEASE_WORKFLOW.md)
- Code-behind currently mixes UI + logic in places (per [DEVELOPMENT-GUIDELINES.md](DEVELOPMENT-GUIDELINES.md))

This is the largest greenfield. We do **not** need DAST or web-CSP audits — printer-app is a desktop app talking to localhost USB/serial. Security focus shifts to: dependency CVEs, code-signing integrity, network calls (only to backend over TLS), and binary-tampering protection.

## 1. Tooling decisions

| Concern | Tool | Why |
|---|---|---|
| Format | `dotnet format` | built-in |
| Lint / static analysis | Roslyn analyzers + `SonarAnalyzer.CSharp` | same as backend |
| Security analyzer | `SecurityCodeScan.VS2019` | weak crypto, deserialization |
| XAML format | `XamlStyler` (CLI) | XAML diff hygiene |
| Test runner | xUnit (new `PrinterAPP.Tests` project) | service-layer unit tests |
| Coverage | `coverlet.collector` → opencover.xml + cobertura | feeds SonarCloud + GitLab MR |
| Vuln check | `dotnet list package --vulnerable --include-transitive` | first-party advisory feed |
| SCA | OSV-Scanner (`packages.lock.json`) | catches CVEs missing from NuGet feed |
| SBOM | `Microsoft.Sbom.Tool` | SPDX 2.3 SBOM artifact for each release |
| SAST | SonarCloud + SonarScanner.MSBuild | quality gate |
| Code-signing | Authenticode `signtool` (existing in [build-windows.ps1](../build-windows.ps1)?) | binary integrity |
| Secret scan | gitleaks (new) + detect-secrets (pre-commit) | belt + suspenders |
| Runner | self-hosted Windows GitLab runner | required for MAUI Windows build (`net9.0-windows10.0.x`) |

## 2. Repository-level changes

### 2.1 Add a test project: `PrinterAPP.Tests`
- xUnit + FluentAssertions + NSubstitute
- Targets `net9.0` (not `net9.0-windows…`) so tests run on Linux runners for fast feedback
- Tests cover: ESC/POS encoder, settings parsing, log rotation, model-vs-backend-DTO contract checks
- Add to `PrinterAPP.sln`

### 2.2 Add `.editorconfig` (root of `printer-app/`)
Same as backend, with extra MAUI-specific rules:
- XAML naming: `x:Name` PascalCase, ResourceDictionary keys camelCase
- Code-behind: `dotnet_diagnostic.IDE0001` warnings as errors so unused usings fail build
- Allow `[XamlCompilation]` attributes

### 2.3 Add `Directory.Build.props` (root of `printer-app/`)
```xml
<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Nullable>enable</Nullable>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="..." PrivateAssets="all"/>
    <PackageReference Include="SonarAnalyzer.CSharp" Version="..." PrivateAssets="all"/>
    <PackageReference Include="SecurityCodeScan.VS2019" Version="..." PrivateAssets="all"/>
  </ItemGroup>
</Project>
```

### 2.4 Add `gitleaks.toml`
Allowlist:
- `PrinterAPP/Resources/Raw/sample-receipts/**`
- Test fixtures under `PrinterAPP.Tests/Fixtures/`
Block: AWS/JWT/SMTP/printer-API-key patterns.

### 2.5 File-length / pattern checker (`scripts/check-quality.ps1` + `.sh`)
Enforces [CLAUDE.md](../../CLAUDE.md) limits:
- Page code-behind > 200 LOC
- Service class > 300 LOC
- Model class > 80 LOC

Forbidden patterns:
- Business logic in `*.xaml.cs` (heuristic: any LINQ chain or `await *Service.*` outside event handlers)
- ESC/POS byte literals in non-`Constants/` files (rule §4 — must be in constants file)
- DTO field rename drift vs backend (cross-check `Models/*.cs` against `backend/RestaurantSystem.Api/Features/**/Dtos/`)

The DTO drift check is unique to this stack — printer-app must mirror backend shapes exactly. The script can:
1. Parse C# records under `printer-app/PrinterAPP/Models/`
2. Parse C# records under `backend/RestaurantSystem.Api/Features/**/Dtos/`
3. Diff field names + types per type-name match; fail on mismatch

## 3. Pre-commit hooks

`.pre-commit-config.yaml` (in `printer-app/`):

```yaml
- id: dotnet-format
  name: dotnet format (verify-no-changes)
  language: system
  entry: bash -c 'dotnet format PrinterAPP.sln --verify-no-changes --no-restore'
  pass_filenames: false
  files: \.(cs|csproj|sln)$
  stages: [pre-commit]

- id: xaml-format
  name: xaml-styler --check
  language: system
  entry: bash -c 'dotnet tool run xstyler -p -r --config .xamlstyler.json'
  pass_filenames: false
  files: \.xaml$
  stages: [pre-commit]

- id: dotnet-build
  name: dotnet build (warnings as errors, net9.0 only)
  language: system
  entry: bash -c 'dotnet build PrinterAPP.Tests/PrinterAPP.Tests.csproj --no-restore -warnaserror'
  pass_filenames: false
  files: \.(cs|csproj)$
  stages: [pre-commit]

- id: file-length-rules
  name: printer-app file length / pattern rules
  language: system
  entry: bash scripts/check-quality.sh
  pass_filenames: false
  files: \.cs$
  stages: [pre-commit]

- id: dto-drift-check
  name: DTO drift vs backend
  language: system
  entry: bash scripts/check-dto-drift.sh
  pass_filenames: false
  files: ^PrinterAPP/Models/.*\.cs$
  stages: [pre-commit]

# Pre-push only — slower
- id: dotnet-test
  name: dotnet test (PrinterAPP.Tests)
  language: system
  entry: bash -c 'dotnet test PrinterAPP.Tests --no-restore'
  pass_filenames: false
  files: \.(cs|csproj)$
  stages: [pre-push]

- id: vulnerable-packages
  name: dotnet list package --vulnerable
  language: system
  entry: bash -c 'dotnet list PrinterAPP.sln package --vulnerable --include-transitive | tee /tmp/vuln.txt; ! grep -E ">.*Critical|>.*High" /tmp/vuln.txt'
  pass_filenames: false
  files: (\.csproj|packages\.lock\.json)$
  stages: [pre-push]
```

Note: `dotnet-build` in pre-commit only builds the **test project** (`net9.0`) so it works on macOS/Linux dev machines. The full Windows build runs in CI on the self-hosted Windows runner.

`detect-secrets` baseline scan paths: `PrinterAPP/`, `PrinterAPP.Tests/`, `scripts/`.

## 4. GitLab CI

New file `printer-app/.gitlab-ci.yml`. Two-runner topology:
- Linux runner: lint, test (test project is `net9.0`), security, SAST
- Windows runner (self-hosted, tag `windows-maui`): MAUI Windows build, code-signing, packaging

### Stages
```
lint → test → security → sast → build → sign → release
```

### Jobs

| Stage | Job | Runner | Image / env | Blocking? |
|---|---|---|---|---|
| lint | `format` (`dotnet format --verify-no-changes`) | linux | `mcr.microsoft.com/dotnet/sdk:9.0@sha256:...` | yes |
| lint | `xaml-format` (XamlStyler --check) | linux | sdk:9.0 + dotnet tool | yes |
| lint | `analyze` (`dotnet build PrinterAPP.Tests -warnaserror`) | linux | sdk:9.0 | yes |
| lint | `quality-rules` (file-length + patterns) | linux | alpine | yes |
| lint | `dto-drift` (cross-repo Models vs backend Dtos) | linux | sdk:9.0 + git checkout backend | yes (warn-only first month) |
| test | `unit-tests` (`dotnet test --collect:"XPlat Code Coverage"`) | linux | sdk:9.0 | yes |
| test | `coverage-gate` (≥ 50% line — UI-heavy, lower than backend) | linux | sdk:9.0 | yes |
| security | `gitleaks` | linux | `zricethezav/gitleaks:v8.x@sha256:...` | yes |
| security | `osv-scanner` (lockfiles) | linux | `ghcr.io/google/osv-scanner@sha256:...` | yes |
| security | `dotnet-vuln` | linux | sdk:9.0 | yes |
| security | `sbom` (`Microsoft.Sbom.Tool`) | linux | sdk:9.0 | no, artifact |
| sast | `sonarcloud` | linux | sdk:9.0 | yes (quality gate) |
| build | `build_windows_msix` | **windows** (tag `windows-maui`) | host PowerShell + MAUI workloads | yes (only on `main`/tag) |
| sign | `code_sign` (`signtool sign`) | windows | host | yes (only on tag) |
| release | `gitlab_release` (attach signed MSIX, SHA256SUMS, SBOM) | linux | `registry.gitlab.com/gitlab-org/release-cli@sha256:...` | manual |

### Coverage report wiring
```yaml
unit-tests:
  artifacts:
    reports:
      coverage_report:
        coverage_format: cobertura
        path: '**/coverage.cobertura.xml'
      junit: '**/TestResults/*.trx'
```

### Workflow rules
```yaml
workflow:
  rules:
    - if: $CI_PIPELINE_SOURCE == 'merge_request_event'
    - if: $CI_COMMIT_BRANCH == 'main'
    - if: $CI_COMMIT_BRANCH == 'dev'
    - if: $CI_COMMIT_TAG               # release tag → build/sign/release
default:
  interruptible: true
```

## 5. SonarCloud config (`printer-app/sonar-project.properties`)

```
sonar.projectKey=rumi_printer_app
sonar.organization=<rumi-org>
sonar.sources=PrinterAPP
sonar.tests=PrinterAPP.Tests
sonar.cs.opencover.reportsPaths=**/coverage.opencover.xml
sonar.cs.vstest.reportsPaths=**/TestResults/*.trx
sonar.exclusions=\
  **/bin/**,**/obj/**,\
  PrinterAPP/Platforms/**,\
  PrinterAPP/Resources/Raw/**
sonar.coverage.exclusions=\
  PrinterAPP/App.xaml.cs,\
  PrinterAPP/AppShell.xaml.cs,\
  PrinterAPP/MauiProgram.cs,\
  **/Converters/**,\
  **/*Page.xaml.cs
```
Quality gate: A-rating, ≥ 50% new-code coverage (lower than backend/frontend due to MAUI UI surface).

## 6. Weekly scheduled pipeline

**Shipped** — `.github/workflows/security-audit.yml` (issue [#6](https://github.com/piwas-21/restaurant-app-printer/issues/6)).

Cron: `0 6 * * 1` (Mondays 06:00 UTC) + `workflow_dispatch`. Runs entirely on
`ubuntu-latest` — **no Windows runner needed** (Windows runners are scarce; this
is the same constraint that defers CodeQL, issue #4). Jobs:

- **OSV-Scanner** — full-tree (`--recursive`) dependency CVE scan.
- **Trivy fs** — filesystem scan, HIGH/CRITICAL, `ignore-unfixed`.
- **gitleaks** — full-history (`fetch-depth: 0`) secret scan; catches secrets
  that predate per-PR scanning, which the diff-scoped PR gate never sees.
- **NuGet vulnerability audit** — `dotnet list package --vulnerable --include-transitive` against `PrinterAPP/PrinterAPP.csproj`. Restores on Linux
  with `-p:EnableWindowsTargeting=true`, which resolves the
  `net9.0-windows10.0.19041.0` TFM reference **without the MAUI workload**
  (verified locally: restore succeeds, audit runs, currently 0 vulnerable
  packages). Direct approach — the OSV-Scanner-only fallback was not needed.

Scheduled runs fail red on findings (visible signal on the Actions tab) with a
job summary. No issue-creation automation. All actions SHA-pinned to match
`ci.yml`.

Still **planned** (Sprint 4, need a Windows runner / release artifacts):
- `dotnet list package --outdated` → artifact
- License audit (`dotnet-project-licenses` — block GPL/AGPL transitives)
- Sensitive-file audit (mirrors DeelMarkt's `infra-security` job — extra scrutiny on `*.pfx`/`*.snk`/`*.cer`)
- **Authenticode verification of last released MSIX** (`signtool verify /pa /v latest.msix`) — alerts if signature drifted

## 7. Phased task breakdown

### Sprint 1 (hygiene + greenfield CI + dev-experience baseline)
1. Commit cross-repo `.pre-commit-config.yaml` (general + secrets)
2. Generate `.secrets.baseline`, list paths in `.secrets-scan-paths` (`PrinterAPP/`, `scripts/`)
3. Create `gitleaks.toml`
4. Add `scripts/setup_hooks.sh` + `.ps1`
5. Provision a self-hosted Windows GitLab runner (tag `windows-maui`); document in `printer-app/README.md`
6. Document branch protection in `printer-app/README.md`
7. Add minimal `.gitlab-ci.yml` with **lint** stage only (`gitleaks`, `dotnet format`) — first green pipeline
8. Add `.gitlab/merge_request_templates/Default.md` — Schema/Contract Verification section names: each `Models/<X>.cs` field touched + matching backend `Features/**/Dtos/` source-of-truth file; the MR description must include the output of `bash scripts/check-dto-drift.sh` for any change under `PrinterAPP/Models/`
9. Add `scripts/dev-up.ps1` (+ `.sh` mirror for cross-platform devs). `dev-up.ps1` orchestrates: health-check backend (default `http://localhost:5000/health`, configurable via `$env:RUMI_API_URL`) → `dotnet workload restore` → `dotnet build PrinterAPP -f net9.0-windows10.0.19041.0` → `dotnet run --project PrinterAPP`. Plus `dev-down.ps1`, `dev-secrets.ps1`.
10. Update `printer-app/README.md` with onboarding: `pwsh -File scripts/setup_hooks.ps1; pwsh -File scripts/dev-up.ps1`
11. Create `docs/adr/` + `ADR-template.md` + `README.md` index. Backfill ADRs:
   - **ADR-001** .NET MAUI choice (vs Avalonia / WinUI 3 / WPF) — single-codebase rationale, current Windows-only constraint, future-platform options
   - **ADR-002** ESC/POS encoder design + constants location — why bytes live in `Constants/` not inline, encoder API shape
   - (ADR-003 and ADR-004 land in Sprint 2)

### Sprint 2 (test project + format + lint + reusable CI templates)
12. Create `PrinterAPP.Tests` xUnit project; one smoke test per service
13. Add `.editorconfig` + `Directory.Build.props` with analyzers + `TreatWarningsAsErrors`
14. Install + configure `XamlStyler` (`.xamlstyler.json`)
15. Add `scripts/check-quality.{sh,ps1}` (file-length + forbidden patterns)
16. Add `scripts/check-dto-drift.sh` (cross-checks Models vs backend Dtos)
17. Add the pre-commit hooks listed in §3
18. **Add `.gitlab/ci/setup-dotnet-linux.yml`** (`.setup-dotnet-linux` for lint/test/SAST jobs — test project targets plain `net9.0`, runs on Linux runners) **and `.gitlab/ci/setup-maui-windows.yml`** (`.setup-maui-windows` for build/sign/release jobs — tag `windows-maui`, `dotnet workload restore`)
19. Expand `.gitlab-ci.yml` lint stage with `format`, `xaml-format`, `analyze`, `quality-rules`, `dto-drift` — all using `extends: .setup-dotnet-linux`
20. Pin all remaining images by digest
21. Backfill remaining ADRs:
   - **ADR-003** Log rotation + retention policy — daily rotation, max-size cap, retention window (compliance-class)
   - **ADR-004** Authenticode signing + MSIX distribution (vs ClickOnce / store) — install trust model, certificate rotation procedure

### Sprint 3 (test + coverage + SAST + Sonar)
16. Wire Coverlet → opencover + cobertura in `PrinterAPP.Tests`
17. Add `unit-tests` + `coverage-gate` jobs (≥ 50% line)
18. Create SonarCloud project, commit `sonar-project.properties`
19. Add `sonarcloud` job; enable MR decoration
20. Add new-code coverage pre-push hook

### Sprint 4 (Windows build, signing, release, deep security)
21. Add `build_windows_msix` job on Windows runner (matches [build-windows.ps1](../build-windows.ps1) commands, parameterised)
22. Move Authenticode signing certificate to GitLab CI/CD variable (file type, masked)
23. Add `code_sign` job invoking `signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256`
24. Add `gitlab_release` job emitting MSIX + `SHA256SUMS` + SBOM as release assets
25. Add `osv-scanner`, `dotnet-vuln`, `sbom`, `trivy fs` security jobs
26. Add weekly schedule pipeline (TruffleHog full, OSV JSON, outdated, license, sensitive-file audit, Authenticode verify)

## 8. Acceptance criteria

- [ ] `pre-commit run --all-files` green from a clean checkout (macOS/Linux dev)
- [ ] MR pipeline blocks on: format, XAML format, analyzer warning, quality-rules, DTO-drift, test, coverage < 50%, vulnerable package, SonarCloud quality gate
- [ ] Test project exists and runs in `<10 s` on Linux
- [ ] Tagged commits produce a signed MSIX + SBOM + SHA256SUMS attached to a GitLab Release
- [ ] No `:latest` or floating tag in `.gitlab-ci.yml`
- [ ] Code-signing cert and timestamp URL stored as masked CI variables, never in repo
- [ ] Weekly pipeline produces SBOM, OSV JSON, outdated-deps, license audit, Authenticode-verify report
