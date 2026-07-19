using System.Collections.ObjectModel;
using System.Linq;
using PrinterAPP.Services;

namespace PrinterAPP;

public partial class ErrorLogsPage : ContentPage
{
    private readonly IRequestLogService _requestLogService;
    private readonly ObservableCollection<LogEntry> _errorLogs;

    public ErrorLogsPage(IRequestLogService requestLogService)
    {
        InitializeComponent();
        _requestLogService = requestLogService;

        // Create filtered collection for errors only
        _errorLogs = new ObservableCollection<LogEntry>();

        // Bind to filtered collection
        ErrorLogsCollectionView.ItemsSource = _errorLogs;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Re-subscribe and reload every time the page is shown. The log service is a singleton that
        // keeps collecting while this page is off-screen; subscribing only in the constructor (and
        // unsubscribing in OnDisappearing) left the page deaf after the first tab switch, so errors
        // logged in the background — e.g. a failing poll — never appeared. Unsubscribe first to keep
        // the handler registered exactly once.
        var logs = (System.Collections.Specialized.INotifyCollectionChanged)_requestLogService.Logs;
        logs.CollectionChanged -= OnLogsCollectionChanged;
        logs.CollectionChanged += OnLogsCollectionChanged;
        RefreshLogs();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Unsubscribe to prevent memory leaks
        ((System.Collections.Specialized.INotifyCollectionChanged)_requestLogService.Logs).CollectionChanged -=
            OnLogsCollectionChanged;
    }

    private void OnLogsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (LogEntry newLog in e.NewItems)
                {
                    if (newLog.Type == LogType.Error || newLog.Type == LogType.PrintError)
                    {
                        _errorLogs.Insert(0, newLog);
                    }
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                RefreshLogs();
            }
        });
    }

    private void RefreshLogs()
    {
        _errorLogs.Clear();

        foreach (var log in _requestLogService.Logs)
        {
            if (log.Type == LogType.Error || log.Type == LogType.PrintError)
            {
                _errorLogs.Add(log);
            }
        }
    }

    private void OnClearLogsClicked(object sender, EventArgs e)
    {
        _requestLogService.ClearLogs();
    }

    // Toggle methods for expandable sections
    private void OnToggleRequestSection(object sender, EventArgs e)
    {
        if (sender is View view && view.BindingContext is LogEntry logEntry)
        {
            logEntry.IsRequestExpanded = !logEntry.IsRequestExpanded;
        }
    }

    private void OnToggleResponseSection(object sender, EventArgs e)
    {
        if (sender is View view && view.BindingContext is LogEntry logEntry)
        {
            logEntry.IsResponseExpanded = !logEntry.IsResponseExpanded;
        }
    }

    private void OnToggleRequestBody(object sender, EventArgs e)
    {
        if (sender is View view && view.BindingContext is LogEntry logEntry)
        {
            logEntry.IsRequestBodyExpanded = !logEntry.IsRequestBodyExpanded;
        }
    }

    private void OnToggleResponseBody(object sender, EventArgs e)
    {
        if (sender is View view && view.BindingContext is LogEntry logEntry)
        {
            logEntry.IsResponseBodyExpanded = !logEntry.IsResponseBodyExpanded;
        }
    }
}
