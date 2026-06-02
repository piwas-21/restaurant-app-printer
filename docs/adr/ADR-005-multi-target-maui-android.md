# ADR-005 — Multi-target MAUI build (Windows + Android; iOS deferred)

**Status:** Accepted
**Date:** 2026-06-01
**Author:** mahmutkaya
**Reviewers:** —
**Implements / supersedes:** Phase 1 of [docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md](../../../docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md); relaxes (does not supersede) [ADR-001 — Windows-only MAUI](ADR-001-windows-only-maui.md)
**References:**
- [PrinterAPP/PrinterAPP.csproj](../../PrinterAPP/PrinterAPP.csproj)
- [global.json](../../global.json)
- [docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md](../../../docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md) (cross-platform plan)

---

## Context

The printer-app is .NET MAUI but [ADR-001](ADR-001-windows-only-maui.md) restricted the build to a single Windows target (`net9.0-windows10.0.19041.0`) because the only deployment was Windows workstations and the printer transport was ~800 LOC of Win32 `winspool.drv` P/Invoke. MAUI itself is fully cross-platform; the Windows-only restriction was a deployment decision, not a technical limit.

The client now wants Android tablets at stations (cheaper hardware, simpler mounting). The [cross-platform plan](../../../docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md) keeps the same MAUI C# codebase and adds an Android target rather than rewriting. Phase 1 is the build-system change that makes both targets compile from one csproj.

Forces:
- **Don't break the live Windows app.** Existing sites keep running the same repo/release; the Windows MSI must stay functionally identical.
- **Dev machines are mixed.** Some developers (and CI) are on non-Windows hosts that physically cannot build a `*-windows` TFM. The build must let them build the Android target without choking on the Windows TFM.
- **Toolchain has moved to .NET 10.** The installed SDK + MAUI workload are .NET 10 / MAUI 10; the project was pinned to net9 / MAUI 9.0.10. Aligning to net10 avoids per-machine net9-workload installs and matches current tooling.
- **iOS has no concrete use case** today; adding it later is a non-breaking second multi-target.

## Decision

We will **multi-target the single `PrinterAPP.csproj`** to **`net10.0-android`** and **`net10.0-windows10.0.19041.0`**, moving the whole app from .NET 9 / MAUI 9 to **.NET 10 / MAUI 10** in the same change.

- The **Windows TFM is included only on Windows hosts** via `Condition="$([MSBuild]::IsOSPlatform('windows'))"`, so non-Windows machines/CI build Android-only while a Windows host (or runner) builds both. This is the standard MAUI multi-target pattern.
- `OutputType` is conditioned per TFM (`WinExe` on Windows to suppress the console window; `Exe` on Android).
- Windows-only dependencies (`System.Management`, used for WMI printer enumeration) move into a Windows-only `<ItemGroup Condition="...windows...">`. The cross-platform `System.Text.Encoding.CodePages` (PC857 Turkish codepage) stays unconditional.
- A `global.json` pins the .NET SDK feature band (`10.0.101`, `rollForward: latestFeature`) so dev machines and CI resolve the same toolchain; `dotnet workload restore` provisions the matching workload.
- Windows-specific code paths remain gated by `#if WINDOWS` (already true for the `winspool.drv` P/Invoke and `System.Management` WMI usage). The MAUI 9→10 bump surfaced a handful of shared-code fixes (the `Microsoft.Maui.FontSize` vs `PrinterAPP.Models.FontSize` ambiguity, the `ReadOnlyObservableCollection.CollectionChanged` explicit-interface access, and `TimePicker.Time` becoming `TimeSpan?`) — these are platform-neutral and apply to both targets.
- **iOS, macOS Catalyst, and Tizen targets remain unbuilt** (their `Platforms/` scaffolding stays in the repo but no TFM references them). iOS is deferred to v2.

ADR-001 is **relaxed, not superseded**: Windows is still a first-class, supported target — it is simply no longer the *only* one.

## Consequences

### Positive
- The same C# codebase (models, polling, ESC/POS formatting, history, request log, UI) now compiles for Android with no language port or new repo.
- Non-Windows developers and CI can build and verify the Android target locally (previously they could build nothing).
- The toolchain matches the installed .NET 10 / MAUI 10, removing net9-workload friction.
- A clean seam exists for Phase 2 (`IPrinterTransport` → `NetworkTcpTransport`), which makes Android actually print.

### Negative
- **The Windows target cannot be built or verified on non-Windows machines** — the OS-conditional Windows TFM means a Mac/Linux build silently produces *only* the Android artifact. The "Windows MSI stays byte-identical" guarantee must be checked on a Windows host / CI runner before any release. This PR's Windows build + MAUI-10 regression is **owed on Windows** and not verified by the author's macOS build.
- Moving to MAUI 10 is a **major framework bump** layered onto the multi-target change; it carries its own regression surface on Windows (rendering, control behaviour) that only Windows QA can sign off.
- Android does not print yet — `WindowsSpoolerTransport` is Windows-only and the network transport lands in Phase 2. Phase 1 delivers a *compiling, launchable* Android app, not a functional one.
- `ApplicationId` is still `com.companyname.printerapp`; the Android Play Console id (`ch.rumirestaurant.printer`) rename is a deploy-time step deferred to avoid changing the live Windows app identity in a code-only change.
