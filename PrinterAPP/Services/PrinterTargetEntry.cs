namespace PrinterAPP.Services;

/// <summary>
/// Whether a printer-target string belongs in the settings screen's <em>network address</em> entry —
/// i.e. a literal IP (<see cref="PrinterEndpoint"/>) or a diagnostic sink
/// (<see cref="PrinterFileSink"/>), as opposed to a Windows spooler name, which comes from the
/// picker instead.
/// </summary>
/// <remarks>
/// Extracted because the settings screen needs this answer in <b>two</b> places that must not drift:
/// the save validator (reject typos) and the load round-trip (put a saved value back in the field).
/// They did drift — the sink was added to <see cref="PrinterTestTarget"/> but neither of these, so a
/// <c>file:</c> target could be <em>tested</em> but never <em>saved</em>: the validator called
/// <see cref="PrinterEndpoint.TryParse"/> alone, rejected it as "not a valid printer IP", and aborted
/// the whole save. The real-order capture — the entire point of the sink — was unreachable from the
/// UI while every unit test passed, because the gap sat in page code-behind that nothing tested.
/// Hence a pure predicate (CLAUDE.md §5.2) rather than a third copy of the condition.
/// </remarks>
public static class PrinterTargetEntry
{
    /// <summary>
    /// <c>true</c> when <paramref name="value"/> is a valid entry for the network-address field.
    /// Blank is <c>false</c> — callers treat "not configured" separately from "configured wrongly".
    /// </summary>
    public static bool IsNetworkFieldTarget(string? value) =>
        PrinterFileSink.TryParse(value, out _) || PrinterEndpoint.TryParse(value, out _, out _);
}
