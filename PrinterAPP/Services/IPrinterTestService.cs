using System.Net;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Sends test receipts so an operator can verify a printer from the settings screen. Routes per
/// target kind through the <see cref="IPrinterTransport"/> seam (network TCP vs Windows spooler).
/// Phases 2b–3 of docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md.
/// </summary>
public interface IPrinterTestService
{
    /// <summary>
    /// Sends a test receipt to a resolved target — network targets over TCP, spooler targets via
    /// the legacy Windows test-receipt path (whose raw-byte send goes through
    /// <see cref="WindowsSpoolerTransport"/>) — and returns a human-readable result line (never
    /// throws for the printer-unreachable case).
    /// </summary>
    /// <param name="target">Resolved target (see <see cref="PrinterTestTarget.Resolve"/>).</param>
    /// <param name="config">Current configuration (spooler test receipts print its values).</param>
    /// <param name="label">Uppercase tag printed on the network test receipt (e.g. "KITCHEN").</param>
    /// <param name="display">Display name used in the result line (e.g. "Kitchen").</param>
    Task<string> TestPrinterAsync(PrinterTestTarget target, PrinterConfiguration config, string label, string display);

    /// <summary>
    /// Sends a small ESC/POS test receipt to a network printer over TCP and returns a
    /// human-readable result line (never throws for the printer-unreachable case).
    /// </summary>
    Task<string> TestNetworkPrinterAsync(IPAddress ip, int port, string label, string display);
}
