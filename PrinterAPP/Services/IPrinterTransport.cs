namespace PrinterAPP.Services;

/// <summary>
/// Abstraction over "how raw ESC/POS bytes reach a printer". Decouples receipt composition
/// (<see cref="OrderPrintService"/>) from the wire: network TCP (cross-platform), the Windows
/// print spooler (legacy), and future Android USB/Bluetooth transports all implement this.
/// Phase 2 of docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md. See ADR-006.
/// </summary>
public interface IPrinterTransport
{
    /// <summary>
    /// True after a send has crossed the transport's delivery boundary and a later failure could
    /// mean that some bytes reached the destination. Callers must not automatically resend then.
    /// </summary>
    bool DeliveryMayHaveOccurred => false;

    /// <summary>
    /// Sends an already-encoded ESC/POS byte payload to the printer. Throws on failure so the
    /// caller can log and surface the error (it does not silently swallow).
    /// </summary>
    Task SendAsync(byte[] data, CancellationToken ct);

    /// <summary>
    /// Probes reachability without printing. Returns <c>true</c> if the printer accepts a
    /// connection, <c>false</c> otherwise. Never throws for the not-reachable case.
    /// </summary>
    Task<bool> TestAsync(CancellationToken ct);
}
