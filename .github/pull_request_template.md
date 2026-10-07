<!--
  Default PR template — RUMI Printer-App
  See CLAUDE.md §6 (pre-implementation verification) and §8 (git workflow).
  Delete sections that don't apply (e.g. Backend contract verification for non-Models changes).
-->

## Summary
<!-- 1–3 bullets describing what this MR does and why. -->
- ...

## Sprint task / issue
<!-- Link the GitHub issue number. -->
- Closes #
- Sprint task:

## Type
- [ ] `feat` — new user-visible feature
- [ ] `fix` — bug fix
- [ ] `refactor` — no behaviour change
- [ ] `chore` — build / CI / dependencies / config
- [ ] `docs` — documentation only
- [ ] `test` — tests only
- [ ] `perf` — performance

## NFR triage (DEV-PHASES-PLAN P1 — one line per touched dimension, "rest: n/a because …")
<!--
  D1 security · D2 performance · D3 cpu/mem (sockets, polling, disposal!) · D4 UX · D5 a11y ·
  D6 i18n · D7 privacy/PII · D8 observability · D9 testing · D10 conventions.
  Auth/API-key/new-endpoint changes: add 3–5 threat-model-lite bullets under D1.
-->
- D…:
- Rest: n/a because …

## Acceptance criteria coverage
<!--
  For each acceptance criterion in the linked issue / sprint task, state coverage.
  Delete this section for chore/docs MRs with no acceptance criteria.
-->

| Criterion | Status | Notes |
|---|---|---|
| <criterion 1> | Covered / Partial / Out of scope | <follow-up issue # if partial> |
| <criterion 2> | | |

## Backend contract verification
<!--
  Required if this MR touches: Models/*.cs, HTTP client code, or auth.
  Delete the whole section if not applicable.
-->

**Backend DTOs referenced / mirrored:**
- `backend/RestaurantSystem.Api/Features/<X>/Dtos/<Y>Dto.cs` — fields: ...

**Cross-repo impact (additive / breaking / none):**
- `backend/`: <none / file affected>

## Standard checklist
- [ ] `dotnet build PrinterAPP.sln` — 0 errors (Windows, locally)
- [ ] No hardcoded backend URLs / API keys / printer names / paths (CLAUDE.md §5)
- [ ] All services have interfaces, registered in `MauiProgram.cs`
- [ ] Code-behind contains only UI event handlers (logic in services)
- [ ] Models match backend DTOs exactly
- [ ] No `null!` on model fields (use `required` or sensible default)
- [ ] Sibling file conventions matched (DI registration, naming)
- [ ] Pre-commit hooks pass locally (`pre-commit run --all-files`)
- [ ] Branch is off `develop`; MR targets `develop` (releases ship via a `develop` → `main` release PR — CLAUDE.md §8)

## Test plan
<!-- Manual testing steps; specific scenarios to verify. For printing-path changes, use installed-build sink/emulator evidence under workspace SD1; Windows build/tests follow SD5. -->
- [ ] ...
- [ ] ...

## Screenshots / printer output
<!-- For UI changes: screenshots. For printing-path changes: include installed-build sink evidence; a receipt photo is optional additional evidence. -->

## Release notes
<!-- Anything operations / users need to know: new config keys, new permissions, breaking config-file changes that require manual user action. -->
- New config keys required: <none / list>
- New secrets required: <none / list>
- Auto-update compatibility: <forward-compatible / requires manual reinstall>
