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

    /// <summary>ESC d 3 — feed 3 lines.</summary>
    public const string Feed3Lines = "\x1B\x64\x03";

    /// <summary>GS V 0 — full cut.</summary>
    public const string FullCut = "\x1D\x56\x00";
}
