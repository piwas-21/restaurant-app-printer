using System.Collections.ObjectModel;

namespace PrinterAPP.Services;

public interface IRequestLogService
{
    ReadOnlyObservableCollection<LogEntry> Logs { get; }
    event EventHandler<LogEntry>? LogAdded;

    void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null);
    void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null);
    void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null);
    // Orders are identified by their ORDER NUMBER (a string), not an int id. Order numbers are
    // yyyyMMdd + a 4-digit sequence (backend OrderNumberGenerator) — a 12-digit value that does not
    // fit in an int, so the old int parameters recorded the parse-failure fallback 0 on every real
    // order. It is also the key the surrounding code already dedups on (EventStreamingService's
    // orderKey), so a log line now joins up with the rest of the pipeline.
    void LogOrderReceived(string orderNumber, Guid? tableId, string? tableLabel, int? tableNumber,
        decimal total, string? orderJson = null, string? source = null);
    void LogPrintRequest(string printerType, string orderNumber, string printerName, string? printContent = null);
    void LogPrintResponse(string printerType, string orderNumber, bool success, string? error = null, string? details = null);
    void LogError(string operation, string message, string? details = null);
    void LogWarning(string operation, string message, string? details = null, string? source = null);
    void ClearLogs();
}
