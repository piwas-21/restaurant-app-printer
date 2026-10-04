using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IPrinterCorrectionCopyService
{
    Task<PrinterCorrectionCopyResult> PrintCopyAsync(
        PrintUpdateJobKey key,
        CancellationToken cancellationToken = default);
}
