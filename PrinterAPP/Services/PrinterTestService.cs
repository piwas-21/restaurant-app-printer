using System.Net;
using System.Text;

namespace PrinterAPP.Services;

/// <summary>
/// Builds and sends ESC/POS test receipts to network printers. Lives in a service (not page
/// code-behind) per CLAUDE.md §5.2; the ESC/POS sequences come from <see cref="EscPosCommands"/>
/// per §5.4. Phase 3 of docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md.
/// </summary>
public class PrinterTestService : IPrinterTestService
{
    public async Task<string> TestNetworkPrinterAsync(IPAddress ip, int port, string label, string display)
    {
        try
        {
            // Phase-2b: the transport is constructed per target IP (a per-printer value), so it is
            // not a DI singleton. A transport factory / per-printer resolution is deferred. See ADR-006.
            var transport = new NetworkTcpTransport(ip, port);
            await transport.SendAsync(BuildTestReceipt(label), CancellationToken.None);
            return $"{display} printer (network {ip}:{port}): ✓ Success";
        }
        catch (Exception ex)
        {
            return $"{display} printer (network {ip}:{port}): ✗ {ex.Message}";
        }
    }

    /// <summary>A minimal ESC/POS test receipt (PC857) incl. Turkish characters, ending with a cut.</summary>
    private static byte[] BuildTestReceipt(string label)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text =
            EscPosCommands.Initialize
            + EscPosCommands.CodepageTurkish
            + EscPosCommands.AlignCenter
            + "RUMI Printer Test\n"
            + label + "\n"
            + "Turkce: çÇğĞşŞüÜıİöÖ\n"
            + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
            + EscPosCommands.AlignLeft
            + EscPosCommands.Feed3Lines
            + EscPosCommands.FullCut;
        return Encoding.GetEncoding(857).GetBytes(text);
    }
}
