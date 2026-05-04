# ADR-001 — Windows-only MAUI target

**Status:** Accepted
**Date:** 2026-04-29
**Author:** mahmutkaya
**Reviewers:** —
**Implements / supersedes:** Initial project scaffolding
**References:**
- [PrinterAPP/PrinterAPP.csproj](../../PrinterAPP/PrinterAPP.csproj) — `<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>`
- [PrinterAPP/global.json](../../PrinterAPP/global.json) — pinned SDK 9.0.100
- [build-windows.ps1](../../build-windows.ps1), [build-windows.sh](../../build-windows.sh)

---

## Context

.NET MAUI supports Windows, macOS (Mac Catalyst), iOS, Android, and Tizen from a single project. The default `dotnet new maui` template builds for all five.

The printer-app exists for one purpose: drive ESC/POS thermal printers connected to the cashier and kitchen workstations of a restaurant client. Those workstations are all running Windows 10/11. The Windows Print Spooler / `winspool.drv` API is the only printing path we use — see [WindowsPrinterService.cs](../../PrinterAPP/Services/WindowsPrinterService.cs).

Carrying the Android / iOS / Mac Catalyst / Tizen targets has real costs:
1. **Build matrix bloat.** Each target framework adds NuGet restore time and CI complexity.
2. **Package surface drift.** A `using` added in a Windows-only file silently breaks the iOS build until someone runs the full matrix.
3. **False feature signals.** Maintaining cross-platform code in a Windows-only product implies portability we don't have or test.

The auto-update flow ([UpdateService.cs](../../PrinterAPP/Services/UpdateService.cs)) downloads a Windows MSI from a GitHub release. There is no plan to ship to other stores.

## Decision

**The project's `<TargetFramework>` is `net9.0-windows10.0.19041.0` only.** All other MAUI target heads (`net9.0-android`, `net9.0-ios`, `net9.0-maccatalyst`, `net9.0-tizen`) are removed from `PrinterAPP.csproj`. Code in `Platforms/Android/`, `Platforms/iOS/`, `Platforms/MacCatalyst/`, `Platforms/Tizen/` is intentionally retained as MAUI scaffolding but not built.

Changing the Windows TFM (e.g. to `windows10.0.22000.0` for newer Windows 11 APIs) is an architecture decision that needs a new ADR. Patch-level SDK bumps via `global.json` are routine.

## Consequences

### Positive
- **Build is fast** — a single TFM, single P/Invoke surface, single MSIX/MSI output.
- **CI cost is low** — no need for a macOS runner (xcodebuild) or Android emulator.
- **No phantom cross-platform code paths.** Windows-only assumptions (drive letters, Win32 P/Invoke, Windows printer API) are fine to make.
- **Distribution is simple** — one MSI per release, consumed by `UpdateService` from a GitHub release.

### Negative
- **No path to deploy to a non-Windows POS** without re-introducing a target. Kitchen tablets (typically Android) are the most plausible future ask.
- **Some MAUI framework bugs are Windows-specific** and we have no cross-platform fallback to triage against.

### Mitigation for the negatives
- If a non-Windows target is ever needed, treat it as a fork and write ADR-NNN to scope the surface (which services need re-implementation, what shared abstractions to extract). Do not add a TFM silently.
- For triage of Windows-specific MAUI bugs, the upstream `dotnet/maui` issue tracker is the source of truth — file there with a minimal repro.

## Alternatives considered

### Alternative A: Keep all default MAUI target heads
Carrying targets we don't ship adds CI minutes, build complexity, and the risk that "fixing the Android build" becomes someone's afternoon. Not justified by any current product need.

### Alternative B: Build a Windows-only WPF app instead of MAUI
WPF is more mature on Windows and has a smaller dependency footprint. But MAUI gives us the option to add a target later (kitchen tablets), and the printer-app's UI is simple enough that MAUI's DataTemplate / XAML works fine. The flexibility is worth more than the marginal WPF maturity gain at this size.

### Alternative C: Native Win32 + WinUI3 only
Even closer to the metal. Same reasoning as Alternative B — gives up future flexibility for marginal gain.
