using System.Collections.ObjectModel;
using System.Collections.Specialized;
using PrinterAPP.Converters;
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

    // Re-subscribe and reload each time the page is shown. The log service is a singleton that keeps
    // collecting while this page is off-screen, so subscribing only in the constructor left the old
    // separate log pages permanently deaf once the user changed tabs — the same defect this unified
    // page also fixes. Unsubscribe first so the handler stays registered exactly once.
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

    // Run synchronously — RequestLogService already raises CollectionChanged on the main thread
    // (it marshals every Add/Clear via MainThread.IsMainThread), so this handler is always on the
    // UI thread. Deferring it again with BeginInvokeOnMainThread reordered a Clear-then-Add pair:
    // the queued Reset's RefreshLogs picked up the already-added entry, then the queued Add inserted
    // it a second time — a duplicate row. Handling the events inline keeps _view in lockstep.
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
    }

    private void RefreshLogs()
    {
        _view.Clear();
        foreach (var log in _requestLogService.Logs.Where(Matches))
        {
            _view.Add(log);
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
        // Application.Current is null in teardown/unit-test lifecycle states; bail rather than NRE.
        if (Application.Current is null)
        {
            return;
        }

        // Active chip fill stays maroon in both modes (white text on it reads fine); inactive chips are
        // outline-only on the page bg, so in dark mode they use the lighter terracotta (maroon-on-aubergine
        // is too low-contrast). Routed through CraftColors (safe token lookup) — see branding P2b, #71.
        var activeFill = CraftColors.Token("Primary", "Primary");
        var accent = CraftColors.Primary;
        foreach (var (chip, name) in new[]
                 {
                     (ChipAll, "All"), (ChipOrders, "Orders"), (ChipErrors, "Errors"), (ChipWarnings, "Warnings"),
                 })
        {
            var active = _filter == name;
            chip.BackgroundColor = active ? activeFill : Colors.Transparent;
            chip.TextColor = active ? Colors.White : accent;
            chip.BorderColor = accent;
            chip.BorderWidth = 1;
        }
    }

    private void OnClearLogsClicked(object sender, EventArgs e) => _requestLogService.ClearLogs();
}
