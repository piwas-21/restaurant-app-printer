using System.Collections.ObjectModel;

namespace PrinterAPP.Services;

public interface IRequestLogService
{
    ReadOnlyObservableCollection<LogEntry> Logs { get; }
    event EventHandler<LogEntry>? LogAdded;

    void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null);
    void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null);
    void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null);
    void LogOrderReceived(int orderId, int? tableNumber, decimal total, string? orderJson = null, string? source = null);
    void LogPrintRequest(string printerType, int orderId, string printerName, string? printContent = null);
    void LogPrintResponse(string printerType, int orderId, bool success, string? error = null, string? details = null);
    void LogError(string operation, string message, string? details = null);
    void LogWarning(string operation, string message, string? details = null, string? source = null);
    void ClearLogs();
}
