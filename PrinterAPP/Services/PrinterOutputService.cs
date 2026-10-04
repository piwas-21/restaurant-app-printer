using System.Text;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public sealed class PrinterOutputService : IPrinterOutputService
{
    private readonly ILogger<PrinterOutputService> _logger;

    public PrinterOutputService(ILogger<PrinterOutputService> logger) => _logger = logger;

    public async Task<KitchenPrintOutcome> SendAsync(
        string printerName,
        string content,
        CancellationToken cancellationToken = default)
    {
        IPrinterTransport? transport = null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var bytes = Encoding.GetEncoding(857).GetBytes(content);
            transport = PrinterTransportResolver.Resolve(printerName);
            WarnWhenCapturingToSink(transport, printerName);

            if (transport is WindowsSpoolerTransport)
            {
                await Task.Run(
                    () => transport.SendAsync(bytes, cancellationToken), cancellationToken);
            }
            else
            {
                await transport.SendAsync(bytes, cancellationToken);
            }

            return KitchenPrintOutcome.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to print to {PrinterName}", printerName);
            return transport?.DeliveryMayHaveOccurred == true
                ? KitchenPrintOutcome.Unknown
                : KitchenPrintOutcome.Failed;
        }
    }

    private void WarnWhenCapturingToSink(IPrinterTransport transport, string printerName)
    {
        if (transport is not FileSinkTransport sink)
            return;

        _logger.LogWarning(
            "DIAGNOSTIC SINK ACTIVE for {PrinterName}: order bytes captured to {Directory}, NOT printed. " +
            "No paper is produced while this target is configured.",
            printerName, sink.CaptureDirectory);
    }
}
