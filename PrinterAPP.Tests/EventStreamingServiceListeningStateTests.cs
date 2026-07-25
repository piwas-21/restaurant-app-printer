using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Pins the <see cref="IEventStreamingService.IsListening"/> state machine.
///
/// <para>This flag is load-bearing in a way that is easy to miss: every restart path
/// (<c>StartListeningAsync</c>, <c>OrderPipeline.StartAsync</c>, <c>OrderPipeline.InitializeAsync</c>)
/// early-returns when it is true. So if the poll loop ever ends while the flag stays true, the feed
/// is not merely stopped — it is <b>unrecoverable</b> without restarting the process, while the UI
/// toggle and the fleet notification both keep claiming it is listening.</para>
///
/// <para>That is exactly what happened when the Android foreground service passed its own
/// CancellationToken into the feed and cancelled it in OnDestroy: the loop broke out, the flag stayed
/// true, and orders silently stopped printing in a process that never died — the original client bug,
/// in a form START_STICKY cannot recover. Regression coverage for ADR-007.</para>
/// </summary>
public sealed class EventStreamingServiceListeningStateTests
{
    // Port 1 on loopback: connections are refused immediately, so the loop runs its error path fast
    // and no test ever touches a real backend.
    private const string UnreachableApi = "http://127.0.0.1:1";

    private static EventStreamingService CreateFeed() => new(
        new StubPrinterService(UnreachableApi),
        new NoopRequestLogService(),
        NullLogger<EventStreamingService>.Instance);

    [Fact]
    public async Task Cancelling_an_externally_owned_token_clears_IsListening()
    {
        var feed = CreateFeed();
        using var cts = new CancellationTokenSource();

        await feed.StartListeningAsync(cts.Token);
        Assert.True(feed.IsListening);

        await cts.CancelAsync();

        Assert.True(
            await WaitUntilAsync(() => !feed.IsListening),
            "IsListening stayed true after the loop was cancelled — the feed would be unrecoverable.");
    }

    // The flag going false is only half of it: what matters operationally is that the feed can then
    // be started again, rather than hitting the "already listening" early-return forever.
    [Fact]
    public async Task The_feed_can_be_restarted_after_an_external_cancellation()
    {
        var feed = CreateFeed();
        using var cts = new CancellationTokenSource();

        await feed.StartListeningAsync(cts.Token);
        await cts.CancelAsync();
        Assert.True(await WaitUntilAsync(() => !feed.IsListening));

        await feed.StartListeningAsync();

        Assert.True(feed.IsListening);
        await feed.StopListeningAsync();
    }

    [Fact]
    public async Task StopListeningAsync_clears_IsListening_and_allows_a_restart()
    {
        var feed = CreateFeed();

        await feed.StartListeningAsync();
        Assert.True(feed.IsListening);

        await feed.StopListeningAsync();
        Assert.False(feed.IsListening);

        await feed.StartListeningAsync();
        Assert.True(feed.IsListening);
        await feed.StopListeningAsync();
    }

    // A blank base URL is a misconfigured install, not a running feed; reporting IsListening would
    // make the fleet heartbeat claim health that does not exist.
    [Fact]
    public async Task An_unconfigured_api_url_does_not_report_listening()
    {
        var feed = new EventStreamingService(
            new StubPrinterService(string.Empty),
            new NoopRequestLogService(),
            NullLogger<EventStreamingService>.Instance);

        await feed.StartListeningAsync();

        Assert.False(feed.IsListening);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        // The loop's first act is an awaited Task.Delay, which cancels promptly — this is a short
        // convergence wait, not a sleep.
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }

        return condition();
    }

    private sealed class StubPrinterService : IPrinterService
    {
        private readonly string _apiBaseUrl;

        public StubPrinterService(string apiBaseUrl) => _apiBaseUrl = apiBaseUrl;

        public string ConfigFilePath => "test-config.json";

        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());

        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) =>
            Task.FromResult(true);

        public Task<System.Net.HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
            Task.FromResult<System.Net.HttpStatusCode?>(System.Net.HttpStatusCode.OK);

        // No API key: these tests exercise the listening state machine, and the feed only adds the
        // header when one is configured. Leaving it blank keeps a credential-shaped literal out of
        // the repo entirely rather than allowlisting one.
        public Task<PrinterConfiguration> LoadConfigurationAsync() =>
            Task.FromResult(new PrinterConfiguration { ApiBaseUrl = _apiBaseUrl });

        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }

    private sealed class NoopRequestLogService : IRequestLogService
    {
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<LogEntry> Logs { get; } =
            new(new System.Collections.ObjectModel.ObservableCollection<LogEntry>());

        public event EventHandler<LogEntry>? LogAdded;

        public void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null) { }
        public void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null) { }
        public void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null) { }
        public void LogOrderReceived(int orderId, int? tableNumber, decimal total, string? orderJson = null, string? source = null) { }
        public void LogPrintRequest(string printerType, int orderId, string printerName, string? printContent = null) { }
        public void LogPrintResponse(string printerType, int orderId, bool success, string? error = null, string? details = null) { }
        public void LogError(string operation, string message, string? details = null) { }
        public void LogWarning(string operation, string message, string? details = null, string? source = null) { }
        public void ClearLogs() => LogAdded?.Invoke(this, new LogEntry());
    }
}
