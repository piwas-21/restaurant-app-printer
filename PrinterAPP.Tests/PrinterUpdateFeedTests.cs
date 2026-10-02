using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class PrinterUpdateFeedTests : IDisposable
{
    private readonly List<FeedServer> _servers = new();

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
    }

    [Fact]
    public async Task Feed_drains_pages_with_equal_timestamps_and_saves_terminal_cursor()
    {
        var timestamp = DateTime.UtcNow.AddMinutes(-2);
        var first = PrinterUpdateTestData.Update(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), createdAt: timestamp, text: "first");
        var second = PrinterUpdateTestData.Update(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), createdAt: timestamp, text: "second");
        var server = AddServer(
            PrinterUpdateTestData.Feed(new[] { first }, "cursor-1", hasMore: true),
            PrinterUpdateTestData.Feed(new[] { second }, "cursor-2"));
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var delivered = new List<PrinterFeedUpdate>();
        var service = CreateFeed(server, store);
        service.UpdateReceived += (_, update) => delivered.Add(update);

        await service.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => store.LoadUpdateCursor() == "cursor-2"));
        await service.StopListeningAsync();

        Assert.Equal(new[] { first.JobId, second.JobId }, delivered.Select(update => update.JobId));
        Assert.Equal(2, store.GetHistory().Count);
        Assert.Equal("cursor-2", store.LoadUpdateCursor());
        Assert.Equal(new[] { (string?)null, "cursor-1" }, server.UpdateCursors.Take(2));
    }

    [Fact]
    public async Task Failed_cursor_save_leaves_cursor_unchanged_and_does_not_fetch_next_page()
    {
        var update = PrinterUpdateTestData.Update(text: "retry me");
        var server = AddServer(PrinterUpdateTestData.Feed(
            new[] { update }, "cursor-1", hasMore: true));
        var store = new RecordingUpdateStore { SaveCursor = false };
        var service = CreateFeed(server, store);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.UpdateReceived += (_, _) => delivered.TrySetResult(true);

        await service.StartListeningAsync();
        Assert.True(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        await service.StopListeningAsync();

        Assert.Null(store.Cursor);
        Assert.Contains(update.Key, store.Records.Keys);
        Assert.DoesNotContain(server.UpdateCursors, cursor => cursor == "cursor-1");
    }

    [Fact]
    public void Parser_rejects_updates_with_an_empty_cursor()
    {
        var update = PrinterUpdateTestData.Update();
        var body = PrinterUpdateTestData.Feed(new[] { update }, " ");

        var result = OrderFeedParser.Parse(body);

        Assert.False(result.IsSuccess);
        Assert.Contains("cursor", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_rejects_an_update_missing_required_text_instead_of_advancing()
    {
        var body = """
        {"data":{"updates":[{"jobId":"11111111-1111-1111-1111-111111111111","revision":1,
        "jobType":"Update","target":"General","orderId":"22222222-2222-2222-2222-222222222222",
        "orderNumber":"202609110001","audience":"Kitchen","createdAt":"2026-09-11T10:00:00Z"}],
        "nextUpdateCursor":"cursor-1","hasMoreUpdates":false}}
        """;

        var result = OrderFeedParser.Parse(body);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Updates);
        Assert.Single(result.UpdateErrors);
        Assert.Contains("text", result.UpdateErrors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_preserves_stable_table_identity_on_update_jobs()
    {
        var update = PrinterUpdateTestData.Update() with
        {
            TableNumber = null,
            TableId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            TableLabel = "T-QA",
        };

        var result = OrderFeedParser.Parse(PrinterUpdateTestData.Feed(new[] { update }, "cursor-1"));

        var parsed = Assert.Single(result.Updates);
        Assert.Equal(update.TableId, parsed.TableId);
        Assert.Equal("T-QA", parsed.TableLabel);
        Assert.Null(parsed.TableNumber);
    }

    [Fact]
    public void Parser_accepts_typed_changes_and_preserves_visit_amendment_and_item_snapshots()
    {
        var update = PrinterUpdateTestData.Update(text: string.Empty) with
        {
            ServiceSessionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            AmendmentId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            AccountRevision = 9,
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Add,
                    Current = new OrderItem
                    {
                        Id = "line-1",
                        ProductName = "Burger",
                        Quantity = 2,
                        IngredientCustomizations = new List<IngredientCustomization>
                        {
                            new() { IngredientId = "onion", IngredientName = "Onion", IsRemoved = true },
                        },
                    },
                },
            },
        };

        var result = OrderFeedParser.Parse(PrinterUpdateTestData.Feed(new[] { update }, "cursor-1"));

        var parsed = Assert.Single(result.Updates);
        Assert.Equal(update.ServiceSessionId, parsed.ServiceSessionId);
        Assert.Equal(update.AmendmentId, parsed.AmendmentId);
        Assert.Equal(9, parsed.AccountRevision);
        var added = Assert.Single(parsed.Changes);
        Assert.Equal(KitchenChangeKind.Add, added.Kind);
        Assert.Equal("Burger", added.Current!.ProductName);
        Assert.Equal(2, added.Current.Quantity);
        Assert.Equal("Onion", Assert.Single(added.Current.IngredientCustomizations!).IngredientName);
    }

    [Fact]
    public void Parser_rejects_typed_change_without_the_snapshot_required_by_its_kind()
    {
        var update = PrinterUpdateTestData.Update() with
        {
            Changes = new[] { new PrinterFeedChange { Kind = KitchenChangeKind.Void } },
        };

        var result = OrderFeedParser.Parse(PrinterUpdateTestData.Feed(new[] { update }, "cursor-1"));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Updates);
        Assert.Contains(result.UpdateErrors, error =>
            error.Message.Contains("invalid item snapshots", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(DevicePrintTarget.General)]
    [InlineData(DevicePrintTarget.Default)]
    [InlineData(DevicePrintTarget.FrontKitchen)]
    [InlineData(DevicePrintTarget.BackKitchen)]
    public void Parser_accepts_each_kitchen_update_target(DevicePrintTarget target)
    {
        var update = PrinterUpdateTestData.Update() with { Target = target };

        var result = OrderFeedParser.Parse(PrinterUpdateTestData.Feed(new[] { update }, "cursor-1"));

        Assert.True(result.IsSuccess);
        Assert.Equal(target, Assert.Single(result.Updates).Target);
    }

    private EventStreamingService CreateFeed(FeedServer server, IPrintUpdateJobStore store) =>
        new(
            new FeedPrinter(server.BaseUrl), new NoopRequestLogService(), new InMemoryFeedCursorStore(),
            NullLogger<EventStreamingService>.Instance, TimeSpan.FromMilliseconds(20), store);

    private FeedServer AddServer(params string[] responses)
    {
        var server = new FeedServer(responses);
        _servers.Add(server);
        return server;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private sealed class FeedPrinter : IPrinterService
    {
        private readonly string _url;
        public FeedPrinter(string url) => _url = url;
        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) => Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) => Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(new PrinterConfiguration
        {
            ApiBaseUrl = _url,
            PrintLanguage = "en",
        });
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }

    private sealed class RecordingUpdateStore : IPrintUpdateJobStore
    {
        public Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> Records { get; } = new();
        public string? Cursor { get; private set; }
        public bool SaveCursor { get; set; } = true;
        public string FilePath => "memory";

        public bool AddOrGet(PrinterFeedUpdate update, out PrintUpdateJobRecord record, out bool shouldDispatch)
        {
            if (Records.TryGetValue(update.Key, out record!))
            {
                shouldDispatch = record.IsPending;
                return true;
            }
            record = new PrintUpdateJobRecord
            {
                Update = update,
                FirstSeenAt = DateTime.UtcNow,
            };
            Records[update.Key] = record;
            shouldDispatch = true;
            return true;
        }

        public IReadOnlyList<PrintUpdateJobRecord> GetPending() =>
            Records.Values.Where(record => record.IsPending).ToList();

        public bool TryBegin(PrintUpdateJobKey key)
        {
            if (!Records.TryGetValue(key, out var record) || !record.IsPending)
                return false;
            Records[key] = record with { State = PrintUpdateJobState.Processing };
            return true;
        }

        public bool Complete(PrintUpdateJobKey key, PrintUpdateJobState state, string? failureReason = null)
        {
            if (!Records.TryGetValue(key, out var record))
                return false;
            Records[key] = record with { State = state, FailureReason = failureReason };
            return true;
        }

        public IReadOnlyList<PrintUpdateJobRecord> GetHistory() => Records.Values.ToList();

        public string? LoadUpdateCursor() => Cursor;

        public bool TryAdvanceUpdateCursor(string? cursor)
        {
            if (!SaveCursor)
                return false;
            Cursor = cursor;
            return true;
        }
    }

    private sealed class FeedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<string> _responses;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serveTask;
        private readonly object _cursorLock = new();
        private readonly List<string?> _updateCursors = new();

        public FeedServer(IEnumerable<string> responses)
        {
            var portProbe = new TcpListener(System.Net.IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _responses = new ConcurrentQueue<string>(responses);
            _serveTask = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string?> UpdateCursors
        {
            get { lock (_cursorLock) { return _updateCursors.ToList(); } }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* best effort */ }
            try { _listener.Close(); } catch { /* best effort */ }
            try { _serveTask.GetAwaiter().GetResult(); } catch { /* best effort */ }
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var cursor = ReadQuery(context.Request.Url, "updateCursor");
                lock (_cursorLock)
                    _updateCursors.Add(cursor);
                var response = _responses.TryDequeue(out var next)
                    ? next
                    : PrinterUpdateTestData.EmptyFeed(cursor);
                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private static string? ReadQuery(Uri? uri, string name)
        {
            var query = uri?.Query.TrimStart('?');
            if (string.IsNullOrWhiteSpace(query))
                return null;
            foreach (var part in query.Split('&'))
            {
                var pieces = part.Split('=', 2);
                if (pieces.Length == 2 && pieces[0] == name)
                    return Uri.UnescapeDataString(pieces[1]);
            }
            return null;
        }
    }
}
