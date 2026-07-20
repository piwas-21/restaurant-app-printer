using System.Collections.ObjectModel;
using System.Net;
using PrinterAPP.Models;
using PrinterAPP.Services;

namespace PrinterAPP.E2E.Support;

/// <summary>
/// Test-only EDGES (docs/E2E-STRATEGY.md §No mocks of our own code) for the order-feed suite. These are
/// NOT mocks of the service under test — <see cref="EventStreamingService"/> runs as the real shipping
/// code. They stand in only for the two things that are genuinely external to the feed logic:
///   • the config source (what a user types into Settings), and
///   • the UI log sink (a surface, not business logic).
/// </summary>

/// <summary>
/// Returns a fixed <see cref="PrinterConfiguration"/> — the config-source edge. Only
/// <see cref="LoadConfigurationAsync"/> is exercised by the feed's polling path; the printer/probe
/// members are not on that path and throw if the test wiring ever drifts onto them.
/// </summary>
public sealed class TestPrinterService : IPrinterService
{
    private readonly PrinterConfiguration _config;

    public TestPrinterService(PrinterConfiguration config) => _config = config;

    public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(_config);

    public string ConfigFilePath => "(e2e in-memory config)";

    public Task<List<string>> GetAvailablePrintersAsync() =>
        throw new NotSupportedException("Not on the feed-poll path under test.");

    public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) =>
        throw new NotSupportedException("Not on the feed-poll path under test.");

    public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
        throw new NotSupportedException("Not on the feed-poll path under test.");

    public Task SaveConfigurationAsync(PrinterConfiguration config) =>
        throw new NotSupportedException("Not on the feed-poll path under test.");
}

/// <summary>No-op request log (the log is a UI surface, not business logic). Captured errors are exposed
/// so a test can assert the feed logged an auth failure without depending on the MAUI log UI.</summary>
public sealed class NoopRequestLogService : IRequestLogService
{
    private readonly List<(string Operation, string Message)> _errors = new();
    private readonly object _gate = new();

    public IReadOnlyList<(string Operation, string Message)> Errors
    {
        get { lock (_gate) { return _errors.ToList(); } }
    }

    public ReadOnlyObservableCollection<LogEntry> Logs { get; } =
        new(new ObservableCollection<LogEntry>());

    // A no-op sink never raises this; the interface still requires it. (CS0067 = "event never used".)
#pragma warning disable CS0067
    public event EventHandler<LogEntry>? LogAdded;
#pragma warning restore CS0067

    public void LogError(string operation, string message, string? details = null)
    {
        lock (_gate) { _errors.Add((operation, message)); }
    }

    public void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null) { }
    public void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null) { }
    public void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null) { }
    public void LogOrderReceived(int orderId, int? tableNumber, decimal total, string? orderJson = null, string? source = null) { }
    public void LogPrintRequest(string printerType, int orderId, string printerName, string? printContent = null) { }
    public void LogPrintResponse(string printerType, int orderId, bool success, string? error = null, string? details = null) { }
    public void LogWarning(string operation, string message, string? details = null, string? source = null) { }
    public void ClearLogs() { }
}
