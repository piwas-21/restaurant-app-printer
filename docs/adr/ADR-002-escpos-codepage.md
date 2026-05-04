# ADR-002 — ESC/POS commands and codepage PC857

**Status:** Accepted
**Date:** 2026-04-29
**Author:** mahmutkaya
**Reviewers:** —
**Implements / supersedes:** Initial print pipeline
**References:**
- [PrinterAPP/Services/OrderPrintService.cs](../../PrinterAPP/Services/OrderPrintService.cs) — receipt composition
- [PrinterAPP/Services/WindowsPrinterService.cs](../../PrinterAPP/Services/WindowsPrinterService.cs) — Win32 raw-print path

---

## Context

Thermal receipt printers used by the client speak [ESC/POS](https://reference.epson-biz.com/modules/ref_escpos/index.php) — Epson's de facto control-character protocol. The protocol mixes printable text with binary command bytes (0x1B prefix sequences for boldness, font size, cut, drawer-kick, etc.).

Three separate concerns interact in the print path:

1. **Command set.** Bold on/off, double-height, alignment, paper cut, cash-drawer pulse — each is a specific byte sequence.
2. **Character encoding.** Receipts include Turkish menu items (`ş`, `ğ`, `ı`, `ç`, `ö`, `ü`) and customer names. Default printer codepage is CP437; Turkish characters print as garbage.
3. **Bypass the Windows GDI print pipeline.** GDI rasterizes text to a bitmap and ships it to the printer, which is slow, fuzzy on thermal heads, and can't trigger the cash drawer or paper cutter. We need to send raw bytes.

The choice of codepage is forced by the hardware: most ESC/POS thermal printers (Epson TM-T20, Star TSP100, generic 80mm units used by the client) support codepage **PC857 (Multilingual Latin / Turkish)** natively. Switching to it via the `ESC t n` command (`0x1B 0x74 13`) makes the firmware translate bytes 0x80–0xFF to the right Turkish glyphs.

## Decision

**ESC/POS commands are sent as raw byte arrays through `WindowsPrinterService` using the Win32 `OpenPrinter` / `WritePrinter` API (P/Invoke), bypassing GDI.**

**Receipt text is encoded as PC857 (Turkish) before being sent.** The printer is switched to codepage PC857 once at the start of every job via `ESC t 13`.

**ESC/POS command constants** are defined in a single constants file, not inline. Inline magic byte sequences in print code are a debugging tarpit (CLAUDE.md §5 rule 4).

## Consequences

### Positive
- **Turkish characters print correctly** without the client maintaining a custom font.
- **Receipts print fast and crisp** — the printer's built-in font path is faster and sharper than GDI rasterization.
- **Cash drawer and paper cutter work** — these are ESC/POS commands, not GDI primitives.
- **Style settings** (bold, double-height) are first-class commands instead of GDI font requests that may or may not survive rasterization.

### Negative
- **Codepage PC857 limits the character set to Turkish + Latin-1.** Cyrillic, Arabic, CJK names won't print correctly without per-job codepage switching.
- **No printer preview** — what we send is what prints. Bugs in command sequencing (e.g. forgetting to reset alignment) cascade across receipt sections.
- **Hardware-specific.** A printer that doesn't support PC857 (rare in the Turkish market, common in EU) will print Turkish characters as garbage. The client's hardware is verified; new deployments need verification.

### Mitigation for the negatives
- Codepage is a runtime setting in the future; if a non-Turkish-locale customer onboards, add a `PrinterConfiguration.Codepage` field and emit `ESC t n` with the right `n` per locale. Don't refactor the constants file every time.
- Any PR that touches `OrderPrintService` requires a physical printer test with a photo attached to the MR (per the MR template's "Screenshots / printer output" section).
- Keep a list of certified printer models in `docs/SECURITY-AUDIT.md` or a dedicated hardware-compat doc.

## Alternatives considered

### Alternative A: GDI / Windows print spooler with custom font
Standard `PrintDocument` API, ship a TTF that has Turkish glyphs. Works but is slow on thermal heads, can't trigger cash drawer / paper cut, and produces visibly worse-quality print than the printer's native font.

### Alternative B: USB raw / serial driver per printer model
Most printers expose a USB / serial endpoint that takes raw ESC/POS. Bypasses Windows print spooler entirely. Faster but kills support for printers shared over LAN (a real client setup) and complicates installation for users (driver install, port discovery).

### Alternative C: Use a third-party ESC/POS .NET library
Libraries like ESCPOS-NET wrap the byte sequences in C# methods. We evaluated and decided to keep our own thin wrapper because (a) we use ~20 commands, (b) keeping our own surface lets us add custom commands (drawer-kick variants per cash register model) without forking, and (c) it's ~200 LOC of constants vs taking on a dependency.
