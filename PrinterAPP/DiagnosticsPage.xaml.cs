using System.Collections.ObjectModel;
using System.Collections.Specialized;
using PrinterAPP.Services;

namespace PrinterAPP;

public partial class DiagnosticsPage : ContentPage
{
    private readonly IRequestLogService _requestLogService;
    private readonly ObservableCollection<LogEntry> _view = new();
    private string _filter = "All";

    public DiagnosticsPage(IRequestLogService requestLogService)
    {
        InitializeComponent();
        _requestLogService = requestLogService;
        LogsCollectionView.ItemsSource = _view;
    }

    // Re-subscribe + reload each time the page is shown. The log service is a singleton that keeps
    // collecting while this page is off-screen; subscribing only in the constructor left the old
    // Logs/Errors/Warnings pages permanently deaf after the first tab switch (the bug this unified
    // page also fixes). Unsubscribe first so the handler stays registered exactly once.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        var logs = (INotifyCollectionChanged)_requestLogService.Logs;
        logs.CollectionChanged -= OnLogsChanged;
        logs.CollectionChanged += OnLogsChanged;
        UpdateChipStyles();
        RefreshLogs();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ((INotifyCollectionChanged)_requestLogService.Logs).CollectionChanged -= OnLogsChanged;
    }

    private void OnFilterClicked(object sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string filter })
        {
            _filter = filter;
            UpdateChipStyles();
            RefreshLogs();
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                    foreach (LogEntry log in e.NewItems)
                    {
                        if (Matches(log))
                        {
                            _view.Insert(0, log); // newest first, matching the source collection
                        }
                    }
                    break;

                // The log service caps at 200 by evicting the oldest entry (a Remove); drop it here
                // too so _view can't grow unbounded or show entries that no longer exist.
                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    foreach (LogEntry log in e.OldItems)
                    {
                        _view.Remove(log);
                    }
                    break;

                default: // Reset (Clear) / Replace / Move — rebuild to stay consistent with the source
                    RefreshLogs();
                    break;
            }
        });
    }

    private void RefreshLogs()
    {
        _view.Clear();
        foreach (var log in _requestLogService.Logs)
        {
            if (Matches(log))
            {
                _view.Add(log);
            }
        }
    }

    private bool Matches(LogEntry log) => _filter switch
    {
        "Orders" => log.Type is LogType.Order or LogType.PrintRequest or LogType.PrintSuccess or LogType.PrintError,
        "Errors" => log.Type is LogType.Error or LogType.PrintError,
        "Warnings" => log.Type == LogType.Warning,
        _ => true, // "All"
    };

    private void UpdateChipStyles()
    {
        var res = Application.Current!.Resources;
        var primary = (Color)res["Primary"];
        // Inactive chips are outline-only on the page background, so in dark mode use the lighter
        // terracotta (maroon-on-aubergine is too low-contrast). Active chips stay maroon + white.
        var accent = Application.Current.RequestedTheme == AppTheme.Dark
            ? (Color)res["PrimaryDark"]
            : primary;
        foreach (var (chip, name) in new[]
                 {
                     (ChipAll, "All"), (ChipOrders, "Orders"), (ChipErrors, "Errors"), (ChipWarnings, "Warnings"),
                 })
        {
            var active = _filter == name;
            chip.BackgroundColor = active ? primary : Colors.Transparent;
            chip.TextColor = active ? Colors.White : accent;
            chip.BorderColor = accent;
            chip.BorderWidth = 1;
        }
    }

    private void OnClearLogsClicked(object sender, EventArgs e) => _requestLogService.ClearLogs();
}
