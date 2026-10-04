using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public sealed class PrinterCorrectionCopyService : IPrinterCorrectionCopyService
{
    private readonly IPrintUpdateJobStore _jobStore;
    private readonly IPrinterService _printerService;
    private readonly IPrinterOutputService _printerOutputService;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<PrinterCorrectionCopyService> _logger;

    public PrinterCorrectionCopyService(
        IPrintUpdateJobStore jobStore,
        IPrinterService printerService,
        IPrinterOutputService printerOutputService,
        IRequestLogService requestLogService,
        ILogger<PrinterCorrectionCopyService> logger)
    {
        _jobStore = jobStore;
        _printerService = printerService;
        _printerOutputService = printerOutputService;
        _requestLogService = requestLogService;
        _logger = logger;
    }

    public async Task<PrinterCorrectionCopyResult> PrintCopyAsync(
        PrintUpdateJobKey key,
        CancellationToken cancellationToken = default)
    {
        var record = _jobStore.GetHistory().FirstOrDefault(item => item.Key == key);
        if (record is null || !IsCopySafeState(record.State))
            return new(false, record?.State, null);

        var update = record.Update;
        var validationError = PrinterFeedUpdateValidation.Validate(update);
        if (validationError is not null)
        {
            _logger.LogWarning("Rejecting invalid correction COPY {JobId}: {Reason}", update.JobId, validationError);
            return new(true, record.State, KitchenPrintOutcome.Unknown);
        }

        var config = await _printerService.LoadConfigurationAsync();
        var destination = UpdateJobRouting.Resolve(update.Target, config);
        if (!destination.IsConfigured || destination.PrinterName is not { Length: > 0 } printerName)
        {
            _logger.LogWarning("No destination configured for correction COPY {JobId}", update.JobId);
            return new(true, record.State, KitchenPrintOutcome.NotConfigured);
        }

        var content = UpdateReceiptComposer.Compose(update, isCopy: true);
        _requestLogService.LogPrintRequest("Kitchen COPY", update.OrderNumber, printerName);
        var outcome = await _printerOutputService.SendAsync(printerName, content, cancellationToken);
        _requestLogService.LogPrintResponse(
            "Kitchen COPY",
            update.OrderNumber,
            outcome.Status == KitchenPrintStatus.Sent,
            outcome.Status == KitchenPrintStatus.Sent
                ? "COPY bytes sent; paper receipt is not confirmed"
                : $"COPY not confirmed: {outcome.Status}");
        _logger.LogInformation("Manual correction COPY {JobId} to {Printer}: {Result}",
            update.JobId, destination.PrinterName, outcome);
        return new(true, record.State, outcome);
    }

    public static bool IsCopySafeState(PrintUpdateJobState state) => state is
        PrintUpdateJobState.Sent or PrintUpdateJobState.Skipped or PrintUpdateJobState.Unknown;
}
