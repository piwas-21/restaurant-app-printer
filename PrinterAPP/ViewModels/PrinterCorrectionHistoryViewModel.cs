using System.ComponentModel;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using PrinterAPP.Services;

namespace PrinterAPP.ViewModels;

public sealed class PrinterCorrectionHistoryViewModel : INotifyPropertyChanged
{
    private const int MaximumVisibleJobs = 100;
    public const string CopyConfirmationWarning =
        "The original correction may already have reached the kitchen. Check with staff before sending a COPY.";

    private readonly IPrintUpdateJobStore _jobStore;
    private readonly IPrinterCorrectionCopyService _copyService;
    private readonly ILogger<PrinterCorrectionHistoryViewModel> _logger;
    private IReadOnlyList<PrinterCorrectionHistoryItem> _history = Array.Empty<PrinterCorrectionHistoryItem>();
    private string _statusMessage = "Recent kitchen updates on this device";

    public PrinterCorrectionHistoryViewModel(
        IPrintUpdateJobStore jobStore,
        IPrinterCorrectionCopyService copyService,
        ILogger<PrinterCorrectionHistoryViewModel> logger)
    {
        _jobStore = jobStore;
        _copyService = copyService;
        _logger = logger;
        RefreshHistory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string CopyWarningText => CopyConfirmationWarning;

    public IReadOnlyList<PrinterCorrectionHistoryItem> History
    {
        get => _history;
        private set => SetProperty(ref _history, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void RefreshHistory()
    {
        try
        {
            History = _jobStore.GetHistory()
                .OrderByDescending(record => record.FirstSeenAt)
                .Take(MaximumVisibleJobs)
                .Select(record => new PrinterCorrectionHistoryItem(record))
                .ToList();
            StatusMessage = "Recent kitchen updates on this device";
        }
        catch (Exception ex)
        {
            StatusMessage = "Correction history could not be loaded";
            _logger.LogWarning(ex, "Could not load local correction history");
        }
    }

    public bool CanCopy(PrintUpdateJobKey key) => _jobStore.GetHistory()
        .FirstOrDefault(record => record.Key == key) is { } record
        && !record.Update.IsWithdrawn
        && PrinterCorrectionCopyService.IsCopySafeState(record.State);

    public Task<PrinterCorrectionCopyResult> PrintCopyAsync(
        PrintUpdateJobKey key,
        CancellationToken cancellationToken = default) =>
        _copyService.PrintCopyAsync(key, cancellationToken);

    public static string CopyResultMessage(PrinterCorrectionCopyResult result)
    {
        if (result.WasWithdrawn)
            return "The server withdrew this correction. No COPY was sent.";

        if (!result.WasEligible)
        {
            return result.OriginalState is null
                ? "This correction is no longer in local history. Refresh and try again."
                : "This correction is still active or retryable. Wait until it reaches a stable result before sending COPY.";
        }

        return result.Outcome?.Status switch
        {
            KitchenPrintStatus.Sent => "COPY bytes were sent. The printer cannot confirm that paper was received.",
            KitchenPrintStatus.Unknown => "Delivery is uncertain. Check the printer before sending another COPY.",
            KitchenPrintStatus.NotConfigured => "No printer is configured for this kitchen station.",
            _ => "The COPY was not sent. Check the printer connection before trying again.",
        };
    }

    private void SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
