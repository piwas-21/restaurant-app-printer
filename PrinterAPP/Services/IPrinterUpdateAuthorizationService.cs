using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IPrinterUpdateAuthorizationService
{
    Task<PrinterUpdateAuthorizationResult> CheckAsync(
        PrinterFeedUpdate update,
        CancellationToken cancellationToken = default);
}
