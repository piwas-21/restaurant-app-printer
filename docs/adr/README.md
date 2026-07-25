# Architecture Decision Records

Index of ADRs for the RUMI Printer-App. New ADRs are numbered sequentially with no gaps.

> **When to write an ADR**: any decision that constrains future implementation choices, has non-obvious tradeoffs, or that you'd want a future agent to understand without re-deriving. Bug fixes don't need ADRs; choosing a library or pattern usually does.
>
> **Format**: copy [ADR-template.md](ADR-template.md) and fill in. Status starts as `Proposed`; flip to `Accepted` on merge.

## Index

| # | Title | Status | Date | Tags |
|---|---|---|---|---|
| [001](ADR-001-windows-only-maui.md) | Windows-only MAUI target | Accepted | 2026-04-29 | platform, build |
| [002](ADR-002-escpos-codepage.md) | ESC/POS commands and codepage PC857 | Accepted | 2026-04-29 | printing, i18n |
| [003](ADR-003-x-api-key-auth.md) | X-Api-Key auth for printer-feed | Accepted | 2026-04-29 | auth, security |
| [004](ADR-004-github-release-auto-update.md) | GitHub-release-based auto-update | Accepted | 2026-04-29 | distribution, release |
| [005](ADR-005-multi-target-maui-android.md) | Multi-target MAUI build (Windows + Android; iOS deferred) | Accepted | 2026-06-01 | platform, build |
| [006](ADR-006-printer-transport-abstraction.md) | IPrinterTransport abstraction; network TCP default | Accepted | 2026-06-01 | printing, platform, testing |
| [007](ADR-007-android-foreground-service.md) | Android background execution via a specialUse foreground service | Accepted | 2026-07-25 | platform, reliability |

## Conventions

- Filename: `ADR-NNN-kebab-case-title.md`
- Numbering: gap-free; if rejected, mark Status: `Rejected` and keep the file (history matters)
- Status progression: `Proposed` → `Accepted` / `Rejected` → `Deprecated` / `Superseded by ADR-XXX`
- One ADR = one decision; if a record grows three sub-decisions, split it
- Reference ADRs from code comments via `// See docs/adr/ADR-NNN.md` when behaviour is non-obvious
