# ADR-006 — `IPrinterTransport` abstraction; network TCP as the default

**Status:** Accepted
**Date:** 2026-06-01
**Author:** mahmutkaya
**Reviewers:** —
**Implements / supersedes:** Phase 2 of [docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md](../../../docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md); builds on [ADR-005](ADR-005-multi-target-maui-android.md)
**References:**
- [PrinterAPP/Services/IPrinterTransport.cs](../../PrinterAPP/Services/IPrinterTransport.cs)
- [PrinterAPP/Services/NetworkTcpTransport.cs](../../PrinterAPP/Services/NetworkTcpTransport.cs)
- [PrinterAPP.Tests/NetworkTcpTransportTests.cs](../../PrinterAPP.Tests/NetworkTcpTransportTests.cs)
- [PrinterAPP/Services/WindowsSpoolerTransport.cs](../../PrinterAPP/Services/WindowsSpoolerTransport.cs) (Phase 2b, first slice: the spooler RAW write behind the seam)
- [PrinterAPP/Services/WindowsPrinterService.cs](../../PrinterAPP/Services/WindowsPrinterService.cs) (the existing Windows path; its test-print send now delegates to the spooler transport)

---

## Context

After [ADR-005](ADR-005-multi-target-maui-android.md) the app compiles for Android, but it cannot yet print there: the only way bytes reach a printer is `OrderPrintService.PrintToWindowsPrinter`, ~800 LOC of `winspool.drv` P/Invoke gated by `#if WINDOWS`. ESC/POS receipt *composition* is already cross-platform (it builds a `string`, then a `byte[]` via PC857); only the final "send these bytes to the printer" step is Windows-bound.

We need a seam so the same composed bytes can go out over different wires — network TCP (works on every platform), the Windows spooler (legacy USB/Bluetooth), and later Android USB-OTG / Bluetooth — without `OrderPrintService` knowing which. The [cross-platform plan](../../../docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md) resolved to keep a **single repo with no extracted Core library**, so the abstraction lives in the app's `Services/` and stays unit-testable by source-linking into a plain test project.

Network thermal printers speak ESC/POS over raw TCP on port **9100** (RAW / JetDirect) — a de-facto standard across Epson, Star, and most brands — so a TCP transport is the natural cross-platform default.

## Decision

We will introduce **`IPrinterTransport`** — `SendAsync(byte[] data, CancellationToken)` and `Task<bool> TestAsync(CancellationToken)` — as the single seam between receipt composition and the wire.

The first implementation is **`NetworkTcpTransport`** (cross-platform, the default):
- Constructor takes `IPAddress` + port (default `9100`); connect/write timeouts and the retry delay are injectable (defaulting to **5s connect, 10s write, 250ms retry**) so the behaviour is deterministically testable.
- Opens a fresh TCP connection per send, writes the exact bytes, flushes.
- **One retry on `ConnectionRefused`** (printer momentarily busy / just power-cycled); **fail-fast** on host-unreachable, host-not-found, and connect timeouts.
- `SendAsync` throws on failure (the caller logs + surfaces it); `TestAsync` returns `false` for the not-reachable case rather than throwing.

It is covered by **golden-byte unit tests** against an in-process `TcpListener` (byte-exact incl. PC857 Turkish bytes, large-payload ordering, the refused-then-succeeds retry, write/probe semantics, constructor validation) — no printer hardware required, runnable on any host.

This ADR introduces the abstraction and the network transport **only**. The follow-up (Phase 2b) refactors the existing Windows P/Invoke into a `WindowsSpoolerTransport : IPrinterTransport` (`#if WINDOWS`), points `OrderPrintService` at the resolved transport per printer, and adds the DI wiring + a `PrinterTransportKind` config field — that change touches the live Windows print path and must be verified on Windows, so it is kept separate.

> **Phase 2b status (2026-07-11):** first slice (2026-07-10) — `WindowsSpoolerTransport` exists (the winspool RAW write moved verbatim behind the seam) and the **Test-Print path** routes through it (`PrinterTestTarget` resolution + `PrinterTestService`, with a `PrinterTransportKind` enum). Second slice (2026-07-11) — the **live order-print path** routes through the seam too: `OrderPrintService.PrintRawContentAsync` resolves per configured printer name via `PrinterTransportResolver` (IP literal → `NetworkTcpTransport`, otherwise → `WindowsSpoolerTransport`) and its duplicate winspool P/Invoke block is deleted. Still pending: persisting a `PrinterTransportKind` per configured printer.

## Consequences

### Positive
- Android can print over WiFi once Phase 2b wires `OrderPrintService` to `NetworkTcpTransport` — no platform-specific printing code on the cross-platform path.
- The central risk Phase 0's hardware spike exists to check (correct, unmodified ESC/POS bytes — including the Turkish PC857 codepage — reaching the printer over TCP) is now pinned by automated tests that run with no hardware.
- A clean extension point for the v1.1 Android USB-OTG / Bluetooth transports (just more `IPrinterTransport` implementations).
- Establishes the repo's **first automated test project**, runnable on macOS/Linux/Windows CI.

### Negative
- The abstraction is added but **not yet consumed** by `OrderPrintService` — until Phase 2b, this is dormant code + tests. (Intentional: isolates the risky production-path rewire from the easily-verifiable transport.)
- The test project **source-links** transport files instead of referencing the MAUI app project (which only multi-targets android/windows). New cross-platform code intended for unit testing must live in a file with no MAUI dependency and be added to the `<Compile Include>` list — a small, explicit coupling.
- `NetworkTcpTransport` opens a connection per send (no pooling). Fine at this volume (a few receipts/minute); revisit only if profiling shows connect overhead matters.
