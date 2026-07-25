using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// The duplicate-ticket regression, end to end against a real HTTP feed.
///
/// <para>The poll cursor and the dedup window used to live only in memory, so every process restart
/// began at <c>UtcNow-30min</c> with an empty dedup set and re-printed every order the backend still
/// reported in that window. That was tolerable while a restart meant a person relaunching the app.
/// It stopped being tolerable when the Android foreground service started restarting itself
/// (START_STICKY, BOOT_COMPLETED) — a crash at the wrong moment would put a second copy of every
/// recent ticket on the pass. Cross-platform plan, Phase 9d.</para>
///
/// <para>Two <c>EventStreamingService</c> instances sharing one cursor store stand in for the same
/// device before and after a restart; the backend keeps serving the same order throughout.</para>
/// </summary>
public sealed class FeedRestartDoesNotReprintTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private int _requestCount;

    public FeedRestartDoesNotReprintTests()
    {
        // Port 0 is not supported by HttpListener, so take a free one from the OS first.
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_baseUrl}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { /* shutting down */ }
        try { _listener.Close(); } catch (Exception) { /* shutting down */ }
    }

    [Fact]
    public async Task An_order_already_printed_is_not_printed_again_after_a_restart()
    {
        var cursorStore = new InMemoryFeedCursorStore();

        // ---- first "process": receives and prints the order
        var first = CreateFeed(cursorStore);
        var firstRun = new List<string>();
        first.OrderReceived += (_, e) => { if (e.Order is not null) firstRun.Add(e.Order.OrderNumber); };

        await first.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => firstRun.Count > 0), "the feed never delivered the order");
        // What OrderPipeline does once the order has printed.
        first.ConfirmOrderHandled("ORD-1001");
        await first.StopListeningAsync();

        Assert.Equal(new[] { "ORD-1001" }, firstRun);

        // ---- second "process": same device, same cursor file, backend still serving that order
        var second = CreateFeed(cursorStore);
        var secondRun = new List<string>();
        second.OrderReceived += (_, e) => { if (e.Order is not null) secondRun.Add(e.Order.OrderNumber); };

        var requestsBefore = Volatile.Read(ref _requestCount);
        await second.StartListeningAsync();

        // Wait for the restarted feed to actually poll, so "nothing printed" cannot pass simply
        // because nothing happened yet.
        Assert.True(
            await WaitUntilAsync(() => Volatile.Read(ref _requestCount) > requestsBefore),
            "the restarted feed never polled");
        await Task.Delay(500);
        await second.StopListeningAsync();

        Assert.True(
            secondRun.Count == 0,
            $"order reprinted after restart: [{string.Join(", ", secondRun)}] — this is the duplicate-ticket bug.");
    }

    // Named for what it actually pins: a confirmed order reaches the cursor. It does NOT distinguish
    // the in-loop write from the stop-flush — both run before the assertion — and it is not meant to.
    [Fact]
    public async Task A_confirmed_order_reaches_the_persisted_cursor()
    {
        var cursorStore = new InMemoryFeedCursorStore();
        var feed = CreateFeed(cursorStore);
        var received = new List<string>();
        feed.OrderReceived += (_, e) => { if (e.Order is not null) received.Add(e.Order.OrderNumber); };

        await feed.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => received.Count > 0));

        // Stands in for OrderPipeline, which confirms only after the order has been through the
        // printers. Until it does, the entry is deliberately NOT persistable.
        Assert.DoesNotContain("ORD-1001", cursorStore.Load().ProcessedOrders.Keys);
        feed.ConfirmOrderHandled("ORD-1001");

        await feed.StopListeningAsync();

        var saved = cursorStore.Load();
        Assert.Contains("ORD-1001", saved.ProcessedOrders.Keys);
        Assert.True(saved.LastPollTime > DateTime.UtcNow.AddMinutes(-5), "the cursor did not advance");
    }

    // The other half of the guarantee, and the one that protects the kitchen rather than the paper
    // roll. An order is marked processed the moment it is dispatched, but printing happens
    // asynchronously afterwards; if the process is killed in between, the order never printed. It
    // must therefore be re-driven on the next start, not suppressed. A missing kitchen ticket means
    // food is never cooked — strictly worse than a duplicate.
    [Fact]
    public async Task An_order_dispatched_but_never_confirmed_is_re_driven_after_a_restart()
    {
        var cursorStore = new InMemoryFeedCursorStore();

        var first = CreateFeed(cursorStore);
        var firstRun = new List<string>();
        first.OrderReceived += (_, e) => { if (e.Order is not null) firstRun.Add(e.Order.OrderNumber); };

        await first.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => firstRun.Count > 0));
        // NO ConfirmOrderHandled — stands in for a kill between dispatch and print.
        await first.StopListeningAsync();

        var second = CreateFeed(cursorStore);
        var secondRun = new List<string>();
        second.OrderReceived += (_, e) => { if (e.Order is not null) secondRun.Add(e.Order.OrderNumber); };

        await second.StartListeningAsync();
        var reDriven = await WaitUntilAsync(() => secondRun.Count > 0);
        await second.StopListeningAsync();

        Assert.True(reDriven, "an unprinted order was suppressed after a restart — the ticket is lost.");
    }

    private EventStreamingService CreateFeed(IFeedCursorStore cursorStore) => new(
        new StubPrinterService(_baseUrl),
        new NoopRequestLogService(),
        cursorStore,
        NullLogger<EventStreamingService>.Instance);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // listener stopped
            }

            Interlocked.Increment(ref _requestCount);

            // Honour modifiedSince exactly as the backend does — a STRICT greater-than on the order's
            // timestamp (backend PrinterFeedQuery.cs:53). This is what makes the cursor half of the
            // guarantee testable: a fixture that always returned the order would hide a persisted
            // cursor that had advanced past it, which loses the ticket just as surely as a stale dedup
            // entry would.
            var modifiedSince = ParseModifiedSince(context.Request.Url);
            var include = modifiedSince is null || OrderTimestamp > modifiedSince.Value;

            var body = Encoding.UTF8.GetBytes(include ? OrderFeedJson : EmptyFeedJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    // Fixed, and in the past, so the fixture can filter on it the way the backend does.
    private static readonly DateTime OrderTimestamp = DateTime.UtcNow.AddMinutes(-5);

    private const string EmptyFeedJson = @"{ ""data"": { ""items"": [] } }";

    private static DateTime? ParseModifiedSince(Uri? url)
    {
        var query = url?.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        // Hand-parsed rather than via HttpUtility: System.Web is not referenced by this test project
        // and the query has exactly one parameter.
        const string key = "modifiedSince=";
        var start = query.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var raw = Uri.UnescapeDataString(query[(start + key.Length)..]);
        return DateTime.TryParse(
            raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    // The printer-feed envelope is { "data": { "items": [ ...orders ] } } — see OrderFeedParser.
    private const string OrderFeedJson = """
        { "data": { "items": [ {
          "id": "11111111-1111-1111-1111-111111111111",
          "orderNumber": "ORD-1001",
          "status": "Confirmed",
          "total": 42.50,
          "tableNumber": 7,
          "type": "DineIn",
          "deliveryAddress": null,
          "items": [{ "productName": "Kebab", "quantity": 1, "price": 42.50 }]
        } ] } }
        """;

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        // The poll loop waits 5s before its first request, so allow well beyond that.
        for (var i = 0; i < 160; i++)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }

        return condition();
    }

    private sealed class StubPrinterService : IPrinterService
    {
        private readonly string _apiBaseUrl;

        public StubPrinterService(string apiBaseUrl) => _apiBaseUrl = apiBaseUrl;

        public string ConfigFilePath => "test-config.json";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) => Task.FromResult(true);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
            Task.FromResult<HttpStatusCode?>(HttpStatusCode.OK);
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
