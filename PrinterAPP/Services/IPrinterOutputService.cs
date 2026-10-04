using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Sends composed ESC/POS content through the configured transport boundary.</summary>
public interface IPrinterOutputService
{
    Task<KitchenPrintOutcome> SendAsync(
        string printerName,
        string content,
        CancellationToken cancellationToken = default);
}
