namespace PrinterAPP.Services;

/// <summary>
/// Which <see cref="IPrinterTransport"/> carries bytes to a printer target. Phase 2 of
/// docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md; see ADR-006. Currently used for test-print
/// target resolution (<see cref="PrinterTestTarget"/>); persisting a kind per configured printer
/// in <see cref="Models.PrinterConfiguration"/> is deferred to the OrderPrintService transport
/// migration (existing config fields stay untouched for backwards compatibility).
/// </summary>
public enum PrinterTransportKind
{
    /// <summary>Raw ESC/POS over TCP (RAW / JetDirect, default port 9100) — cross-platform.</summary>
    NetworkTcp,

    /// <summary>The legacy Windows print spooler (winspool.drv RAW datatype) — Windows only.</summary>
    WindowsSpooler,

    /// <summary>
    /// Diagnostic sink: write the ESC/POS bytes to a file instead of a printer
    /// (<see cref="FileSinkTransport"/>, target syntax <c>file:[dir]</c>) — cross-platform.
    /// Lets the byte stream be inspected without thermal hardware; not a printing configuration.
    /// </summary>
    FileSink,
}
