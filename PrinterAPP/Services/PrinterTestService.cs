using System.Net;
using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Builds and sends ESC/POS test receipts, routing per target kind: file-sink targets are written
/// to disk (<see cref="FileSinkTransport"/>); network targets go over
/// <see cref="NetworkTcpTransport"/>; Windows spooler targets delegate to the legacy
/// <see cref="IPrinterService.PrintTestReceiptAsync"/> path (thermal detection, retries,
/// port/HTML fallbacks — byte-identical to before), whose raw spooler write goes through
/// <see cref="WindowsSpoolerTransport"/>. Lives in a service (not page code-behind) per CLAUDE.md
/// §5.2; the ESC/POS sequences come from <see cref="EscPosCommands"/> per §5.4. Phases 2b–3 of
/// docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md.
/// </summary>
public class PrinterTestService : IPrinterTestService
{
    private readonly IPrinterService _printerService;

    public PrinterTestService(IPrinterService printerService)
    {
        _printerService = printerService;
    }

    public async Task<string> TestPrinterAsync(
        PrinterTestTarget target, PrinterConfiguration config, string label, string display)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(config);

        // Pattern-matched rather than `target.SinkDirectory!`: Kind == FileSink does guarantee the
        // directory (PrinterTestTarget.Resolve sets them together), but asserting that with a
        // null-forgiving operator buys nothing and turns a future inconsistency into a
        // NullReferenceException inside the transport. The unreachable branch answers in the same
        // shape as this method's other refusals.
        if (target.Kind == PrinterTransportKind.FileSink)
            return target.SinkDirectory is { } sinkDirectory
                ? await TestFileSinkAsync(sinkDirectory, label, display)
                : $"{display} printer: ✗ Sink target '{target.RawText}' resolved without a directory";

        if (target.Kind == PrinterTransportKind.NetworkTcp)
        {
            if (!target.IsValid)
                return $"{display} printer: ✗ Invalid IP '{target.RawText}'";
            return await TestNetworkPrinterAsync(target.Ip!, target.Port, label, display);
        }

        var success = await _printerService.PrintTestReceiptAsync(target.PrinterName!, config);
        return $"{display} printer: {(success ? "✓ Success" : "✗ Failed")}";
    }

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

    /// <summary>
    /// Writes the test receipt to the diagnostic file sink and reports the DIRECTORY back, because
    /// a capture nobody can find is no better than no capture — there is no paper to look at.
    /// </summary>
    public async Task<string> TestFileSinkAsync(string directory, string label, string display)
    {
        try
        {
            var transport = new FileSinkTransport(directory);
            await transport.SendAsync(BuildTestReceipt(label), CancellationToken.None);
            return $"{display} printer (file sink): ✓ Wrote capture to {transport.CaptureDirectory}";
        }
        catch (Exception ex)
        {
            return $"{display} printer (file sink {directory}): ✗ {ex.Message}";
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
