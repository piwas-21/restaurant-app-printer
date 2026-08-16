using PrinterAPP.Models;
using PrinterAPP.Services;

namespace PrinterAPP.Tests;

/// <summary>
/// An <see cref="IRequestLogService"/> that records nothing — the request log is not what any of
/// these suites are asserting, but every service that touches the feed takes one.
///
/// <para><see cref="LogError"/> and <see cref="LogWarning"/> are virtual so a suite that DOES care
/// about the operator-facing text can subclass and capture just those two.</para>
/// </summary>
public class NoopRequestLogService : IRequestLogService
{
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<LogEntry> Logs { get; } =
        new(new System.Collections.ObjectModel.ObservableCollection<LogEntry>());

    public event EventHandler<LogEntry>? LogAdded;

    public void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null) { }
    public void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null) { }
    public void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null) { }
    public void LogOrderReceived(string orderNumber, int? tableNumber, decimal total, string? orderJson = null, string? source = null) { }
    public void LogPrintRequest(string printerType, string orderNumber, string printerName, string? printContent = null) { }
    public void LogPrintResponse(string printerType, string orderNumber, bool success, string? error = null, string? details = null) { }
    public virtual void LogError(string operation, string message, string? details = null) { }
    public virtual void LogWarning(string operation, string message, string? details = null, string? source = null) { }
    public void ClearLogs() => LogAdded?.Invoke(this, new LogEntry());
}
