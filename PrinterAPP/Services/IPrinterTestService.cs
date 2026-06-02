using System.Net;

namespace PrinterAPP.Services;

/// <summary>
/// Sends test receipts so an operator can verify a printer from the settings screen. Phase 3 of
/// docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md (network/Android printers).
/// </summary>
public interface IPrinterTestService
{
    /// <summary>
    /// Sends a small ESC/POS test receipt to a network printer over TCP and returns a
    /// human-readable result line (never throws for the printer-unreachable case).
    /// </summary>
    Task<string> TestNetworkPrinterAsync(IPAddress ip, int port, string label, string display);
}
