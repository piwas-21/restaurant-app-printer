using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public sealed class PrinterCorrectionCopyService : IPrinterCorrectionCopyService
{
    private readonly IPrintUpdateJobStore _jobStore;
    private readonly IPrinterService _printerService;
    private readonly IPrinterOutputService _printerOutputService;
    private readonly IPrinterUpdateAuthorizationService _authorizationService;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<PrinterCorrectionCopyService> _logger;

    public PrinterCorrectionCopyService(
        IPrintUpdateJobStore jobStore,
        IPrinterService printerService,
        IPrinterOutputService printerOutputService,
        IPrinterUpdateAuthorizationService authorizationService,
        IRequestLogService requestLogService,
        ILogger<PrinterCorrectionCopyService> logger)
    {
        _jobStore = jobStore;
        _printerService = printerService;
        _printerOutputService = printerOutputService;
        _authorizationService = authorizationService;
        _requestLogService = requestLogService;
        _logger = logger;
    }

    public async Task<PrinterCorrectionCopyResult> PrintCopyAsync(
        PrintUpdateJobKey key,
        CancellationToken cancellationToken = default)
    {
        var record = _jobStore.GetHistory().FirstOrDefault(item => item.Key == key);
        if (record is null)
            return new(false, null, null);
        if (record.Update.IsWithdrawn)
            return new(false, record.State, null, WasWithdrawn: true);
        if (!IsCopySafeState(record.State))
            return new(false, record.State, null);

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
        PrinterUpdateAuthorizationResult authorization;
        try
        {
            authorization = await _authorizationService.CheckAsync(update, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Authorization check failed immediately before correction COPY {JobId}; no printer bytes were sent",
                update.JobId);
            return new(true, record.State, KitchenPrintOutcome.Failed);
        }

        if (authorization.Status == PrinterUpdateAuthorizationStatus.Withdrawn)
        {
            if (!_jobStore.MarkWithdrawn(key))
                _logger.LogError("Could not persist server withdrawal for correction {JobId}", update.JobId);
            return new(false, record.State, KitchenPrintOutcome.Skipped, WasWithdrawn: true);
        }
        if (authorization.Status != PrinterUpdateAuthorizationStatus.Authorized)
        {
            _logger.LogWarning(
                "Correction COPY authorization is unavailable for job {JobId}; no printer bytes were sent",
                update.JobId);
            return new(true, record.State, KitchenPrintOutcome.Failed);
        }
        if (_jobStore.IsWithdrawalRequested(key))
            return new(false, record.State, KitchenPrintOutcome.Skipped, WasWithdrawn: true);

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
