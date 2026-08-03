using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace PrinterAPP.Services;

// LogType + LogEntry moved to LogEntry.cs (MAUI-free, so the headless E2E harness can source-link the
// IRequestLogService boundary). This class keeps MAUI's MainThread marshalling — the E2E harness uses a
// no-op IRequestLogService instead of this concrete UI-bound service.

public class RequestLogService : IRequestLogService
{
    private readonly ILogger<RequestLogService> _logger;
    private readonly ObservableCollection<LogEntry> _logs;
    private readonly object _lockObject = new();

    // External callers see the read-only wrapper so they cannot bypass
    // the 200-item cap or the _lockObject-guarded mutators below by
    // calling .Add() / .Clear() directly on the inner collection.
    // CollectionChanged events propagate through the wrapper.
    public ReadOnlyObservableCollection<LogEntry> Logs { get; }

    public event EventHandler<LogEntry>? LogAdded;

    public RequestLogService(ILogger<RequestLogService> logger)
    {
        _logger = logger;
        _logs = new ObservableCollection<LogEntry>();
        Logs = new ReadOnlyObservableCollection<LogEntry>(_logs);
    }

    public void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null)
    {
        var source = endpoint.ToLower() == "kitchen" ? "Kitchen" : endpoint.ToLower() == "service" ? "Service" : "General";
        var entry = CreateLogEntry(LogType.SSE, $"SSE {endpoint}", status, string.Empty, source);
        entry.RequestUrl = url;
        entry.RequestHeaders = headers;
        AddLogEntry(entry);
    }

    public void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null)
    {
        var source = endpoint.ToLower() == "kitchen" ? "Kitchen" : endpoint.ToLower() == "service" ? "Service" : "General";
        var entry = CreateLogEntry(LogType.SSE, $"SSE Response {endpoint}", $"Status: {statusCode}", string.Empty, source);
        entry.ResponseStatusCode = statusCode;
        entry.ResponseHeaders = responseHeaders;
        AddLogEntry(entry);
    }

    public void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null)
    {
        var entry = CreateLogEntry(LogType.SSE, $"Event: {eventType}", "Received", data, source ?? "General");
        entry.ResponseBody = rawData ?? data;
        AddLogEntry(entry);
    }

    public void LogOrderReceived(string orderNumber, int? tableNumber, decimal total, string? orderJson = null, string? source = null)
    {
        var entry = CreateLogEntry(LogType.Order, OrderLabel(orderNumber), $"Table {tableNumber}", $"${total:F2}", source ?? "General");
        entry.ResponseBody = orderJson;
        AddLogEntry(entry);
    }

    public void LogPrintRequest(string printerType, string orderNumber, string printerName, string? printContent = null)
    {
        var source = printerType.ToLower() == "kitchen" ? "Kitchen" : "Service";
        var entry = CreateLogEntry(LogType.PrintRequest, $"{printerType} Print", OrderLabel(orderNumber), $"Printer: {printerName}", source);
        entry.RequestBody = printContent;
        AddLogEntry(entry);
    }

    public void LogPrintResponse(string printerType, string orderNumber, bool success, string? error = null, string? details = null)
    {
        var source = printerType.ToLower() == "kitchen" ? "Kitchen" : "Service";
        var status = success ? "✓ Success" : "✗ Failed";
        var message = error ?? "Printed successfully";
        var entry = CreateLogEntry(success ? LogType.PrintSuccess : LogType.PrintError,
               $"{printerType} Result",
               $"{OrderLabel(orderNumber)} - {status}",
               message,
               source);
        entry.ResponseBody = details;
        AddLogEntry(entry);
    }

    public void LogError(string operation, string message, string? details = null)
    {
        var entry = CreateLogEntry(LogType.Error, operation, $"Error: {message}", details ?? string.Empty, "General");
        AddLogEntry(entry);
    }

    public void LogWarning(string operation, string message, string? details = null, string? source = null)
    {
        var entry = CreateLogEntry(LogType.Warning, operation, $"Warning: {message}", details ?? string.Empty, source ?? "General");
        AddLogEntry(entry);
    }

    // Order.OrderNumber is declared non-nullable, but it is populated by JSON deserialization —
    // an explicit `"orderNumber": null` in the feed lands here as null regardless. A bare "Order #"
    // would read like a rendering bug, so name the gap instead.
    private static string OrderLabel(string? orderNumber) =>
        string.IsNullOrWhiteSpace(orderNumber) ? "Order #(no number)" : $"Order #{orderNumber}";

    private LogEntry CreateLogEntry(LogType type, string operation, string message, string details, string source = "General")
    {
        return new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Type = type,
            Operation = operation,
            Message = message,
            Details = details,
            Source = source
        };
    }

    private void AddLogEntry(LogEntry entry)
    {
        // `_logs` is bound to the log pages' CollectionViews; mutating an ObservableCollection off
        // the UI thread throws in MAUI. Log calls frequently originate from background threads
        // (SSE event streaming), so marshal to the UI thread.
        if (MainThread.IsMainThread)
        {
            AddLogEntryInternal(entry);
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => AddLogEntryInternal(entry));
        }
    }

    private void AddLogEntryInternal(LogEntry entry)
    {
        try
        {
            lock (_lockObject)
            {
                // Insert at the beginning (most recent first)
                _logs.Insert(0, entry);

                // Log to console/debug
                _logger.LogInformation("[{Type}] {Operation}: {Message}", entry.Type, entry.Operation, entry.Message);

                // Notify subscribers
                LogAdded?.Invoke(this, entry);

                // Keep only last 200 logs to prevent memory issues
                if (_logs.Count > 200)
                {
                    _logs.RemoveAt(_logs.Count - 1);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding log entry");
        }
    }

    public void ClearLogs()
    {
        if (MainThread.IsMainThread)
        {
            ClearLogsInternal();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(ClearLogsInternal);
        }
    }

    private void ClearLogsInternal()
    {
        lock (_lockObject)
        {
            _logs.Clear();
            _logger.LogInformation("Request logs cleared");
        }
    }
}
