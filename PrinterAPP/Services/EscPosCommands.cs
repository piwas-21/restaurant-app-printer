namespace PrinterAPP.Services;

/// <summary>
/// Named ESC/POS command sequences (string literals, encoded to PC857 before sending). Keeps the
/// magic byte sequences out of call sites (printer-app CLAUDE.md §5.4). These mirror the codes used
/// in <see cref="OrderPrintService"/>; extracted here for the shared network test-receipt path.
/// </summary>
public static class EscPosCommands
{
    /// <summary>ESC @ — initialize / reset the printer.</summary>
    public const string Initialize = "\x1B\x40";

    /// <summary>ESC t 9 — select code page PC857 (Turkish MS-DOS). See ADR-002.</summary>
    public const string CodepageTurkish = "\x1B\x74\x09";

    /// <summary>ESC a 1 — center alignment.</summary>
    public const string AlignCenter = "\x1B\x61\x01";

    /// <summary>ESC a 0 — left alignment.</summary>
    public const string AlignLeft = "\x1B\x61\x00";

    /// <summary>ESC a 2 — right alignment.</summary>
    public const string AlignRight = "\x1B\x61\x02";

    /// <summary>ESC E 1 — bold on.</summary>
    public const string BoldOn = "\x1B\x45\x01";

    /// <summary>ESC E 0 — bold off.</summary>
    public const string BoldOff = "\x1B\x45\x00";

    /// <summary>ESC G 1 — emphasized (double-strike) on.</summary>
    public const string EmphasizedOn = "\x1B\x47\x01";

    /// <summary>ESC G 0 — emphasized off.</summary>
    public const string EmphasizedOff = "\x1B\x47\x00";

    /// <summary>GS ! 0 — normal size (1x width, 1x height).</summary>
    public const string SizeNormal = "\x1D\x21\x00";

    /// <summary>GS ! 1 — tall only (1x width, 2x height). Same code OrderPrintService uses.</summary>
    public const string SizeTall = "\x1D\x21\x01";

    /// <summary>GS ! 16 — wide only (2x width, 1x height). Same code OrderPrintService uses.</summary>
    public const string SizeWide = "\x1D\x21\x10";

    /// <summary>GS ! 17 — double (2x width, 2x height).</summary>
    public const string SizeDouble = "\x1D\x21\x11";

    /// <summary>GS ! 34 — large (2x width, 3x height).</summary>
    public const string SizeLarge = "\x1D\x21\x22";

    /// <summary>ESC d 3 — feed 3 lines.</summary>
    public const string Feed3Lines = "\x1B\x64\x03";

    /// <summary>GS V 0 — full cut.</summary>
    public const string FullCut = "\x1D\x56\x00";
}
