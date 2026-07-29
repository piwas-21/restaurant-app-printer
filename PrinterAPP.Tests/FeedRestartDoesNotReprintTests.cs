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

    // xUnit builds a fresh instance per test, so each one sets this for itself.
    private bool _filterByModifiedSince;

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
        // Backend keeps re-emitting the order regardless of modifiedSince. That is the realistic
        // shape here: an UpdatedAt bump pushes an already-printed order back inside the window, which
        // is exactly what the 1-hour dedup set exists to absorb. It also keeps the dedup set
        // load-bearing in this test — with a filtering backend the restored cursor would exclude the
        // order by itself and the assertion would pass even with the dedup restore deleted.
        _filterByModifiedSince = false;

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
        _filterByModifiedSince = false;

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
        // Backend filters on modifiedSince exactly as the real one does (strict greater-than,
        // PrinterFeedQuery.cs:53). That is what makes the persisted-cursor floor the property under
        // test: without it the restored cursor advances past the unconfirmed order and it is never
        // re-fetched at all.
        _filterByModifiedSince = true;

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

    // Covers the CALL SITE, which the pure UnconfirmedOrderExpiry tests cannot: deleting the
    // ExpireUnrecoverableUnconfirmedOrders() call from CleanupOldProcessedOrders reverts the whole
    // retention fix — unconfirmed orders go back to outliving the cursor look-back clamp, their floor
    // is silently discarded on load, and nothing tells the operator. That deletion passed every other
    // test in this suite.
    //
    // A fresh InMemoryFeedCursorStore starts at UtcNow - MaxLookBack (30 min), which is already past
    // the 25-minute retention, so the order dispatched on the first poll expires on the next one —
    // no clock injection and no 25-minute wait needed.
    [Fact]
    public async Task An_unconfirmed_order_too_old_to_recover_is_reported_to_the_operator()
    {
        _filterByModifiedSince = false;
        AssertFreshCursorStartsPastRetention();

        var log = new CapturingRequestLogService();
        var feed = new EventStreamingService(
            new StubPrinterService(_baseUrl), log, new InMemoryFeedCursorStore(),
            NullLogger<EventStreamingService>.Instance);

        var received = new List<string>();
        feed.OrderReceived += (_, e) => { if (e.Order is not null) received.Add(e.Order.OrderNumber); };

        await feed.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => received.Count > 0), "the feed never delivered the order");

        // Never confirmed, and its poll window is already beyond recovery — the next cleanup must say so.
        var reported = await WaitUntilAsync(() =>
            log.Errors.Any(e => e.Contains("too old to fetch", StringComparison.OrdinalIgnoreCase)));
        await feed.StopListeningAsync();

        Assert.True(
            reported,
            "an unconfirmed order aged past recovery without telling anyone — the ticket is lost silently. "
            + $"Errors seen: [{string.Join(" | ", log.Errors)}]");
    }

    // A print slower than the retention window gets its order declared unrecoverable, and the operator
    // is told it "may not have printed... reprint it". If the print then completes and they act on
    // that stale advice, they produce exactly the duplicate ticket this work exists to prevent — so
    // the warning has to be retracted explicitly.
    [Fact]
    public async Task A_print_that_completes_after_being_declared_unrecoverable_retracts_the_warning()
    {
        _filterByModifiedSince = false;
        AssertFreshCursorStartsPastRetention();

        var log = new CapturingRequestLogService();
        var feed = new EventStreamingService(
            new StubPrinterService(_baseUrl), log, new InMemoryFeedCursorStore(),
            NullLogger<EventStreamingService>.Instance);

        var received = new List<string>();
        feed.OrderReceived += (_, e) => { if (e.Order is not null) received.Add(e.Order.OrderNumber); };

        await feed.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => received.Count > 0));
        Assert.True(
            await WaitUntilAsync(() => log.Errors.Any(e => e.Contains("too old to fetch", StringComparison.OrdinalIgnoreCase))),
            "the order was never declared unrecoverable, so there is nothing to retract");

        // The slow print finally lands.
        feed.ConfirmOrderHandled("ORD-1001");
        await feed.StopListeningAsync();

        Assert.Contains(
            log.Warnings,
            w => w.Contains("did print after all", StringComparison.OrdinalIgnoreCase));
    }

    // Both expiry tests work because a fresh cursor store starts at UtcNow - MaxLookBack, which is a
    // fixed 5 minutes past UnconfirmedRetention — so the first poll's order is already unrecoverable
    // and expires on the next cleanup, with no clock injection. That is load-bearing but not obvious,
    // so it is stated: if the margin is ever collapsed these tests would still fail, but with "the
    // feed never reported it", which sends the next reader hunting in entirely the wrong place.
    private static void AssertFreshCursorStartsPastRetention() =>
        Assert.True(
            FeedCursorStore.MaxLookBack > EventStreamingService.UnconfirmedRetention,
            "these tests rely on a fresh cursor starting already past the retention window");

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

            // Per-test, because the two guarantees need opposite backends and a single global
            // behaviour lets them mask each other — with filtering on, breaking the dedup restore is
            // invisible (the cursor already excludes the order), and with it off, breaking the cursor
            // floor is invisible (the order is served anyway). Each test selects the backend that
            // makes ITS property load-bearing; see the comments on each.
            var include = !_filterByModifiedSince
                || ParseModifiedSince(context.Request.Url) is not { } since
                || OrderTimestamp > since;

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

    /// <summary>Records the operator-facing text, which is the contract under test.</summary>
    private sealed class CapturingRequestLogService : NoopRequestLogService
    {
        private readonly List<string> _errors = new();
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Errors
        {
            get { lock (_errors) { return _errors.ToList(); } }
        }

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) { return _warnings.ToList(); } }
        }

        public override void LogError(string operation, string message, string? details = null)
        {
            // The poll loop is a background thread; the assertion polls from the test thread.
            lock (_errors)
            {
                _errors.Add($"{operation}: {message}");
            }
        }

        public override void LogWarning(
            string operation, string message, string? details = null, string? source = null)
        {
            lock (_warnings)
            {
                _warnings.Add($"{operation}: {message}");
            }
        }
    }

    private class NoopRequestLogService : IRequestLogService
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
}
