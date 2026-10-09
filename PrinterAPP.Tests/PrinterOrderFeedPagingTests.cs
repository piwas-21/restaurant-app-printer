using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class PrinterOrderFeedPagingTests : IDisposable
{
    private readonly List<FeedServer> _servers = new();

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
    }

    [Fact]
    public async Task Poll_drains_all_order_pages_with_the_same_time_and_update_cursor()
    {
        var firstPage = Enumerable.Range(1, 50).Select(OrderJson).ToArray();
        var secondPage = new[] { OrderJson(51), OrderJson(52) };
        var server = AddServer(request =>
        {
            if (request.OrderCursor is null)
                return Task.FromResult(Feed(firstPage, hasMore: true, nextOrderCursor: "opaque cursor/next?x=1"));
            return Task.FromResult(Feed(secondPage, hasMore: false));
        });
        var cursorStore = new InMemoryFeedCursorStore();
        cursorStore.Save(new FeedCursor
        {
            LastPollTime = DateTime.UtcNow.AddMinutes(-2),
            LastUpdateCursor = "update-start",
            ProcessedOrders = new Dictionary<string, DateTime>(),
        });
        var service = CreateFeed(server, cursorStore);
        var received = new ConcurrentQueue<string>();
        service.OrderReceived += (_, args) =>
        {
            if (args.Order is not null)
            {
                received.Enqueue(args.Order.OrderNumber);
                service.ConfirmOrderHandled(args.Order.OrderNumber);
            }
        };

        await service.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => received.Count == 52), "the poll did not drain all 52 orders");
        await service.StopListeningAsync();

        Assert.Equal(52, received.Distinct().Count());
        var requests = server.Requests.Take(2).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Null(requests[0].OrderCursor);
        Assert.Equal("opaque cursor/next?x=1", requests[1].OrderCursor);
        Assert.All(requests, request => Assert.Equal("update-start", request.UpdateCursor));
        Assert.Equal(requests[0].ModifiedSince, requests[1].ModifiedSince);
    }

    [Fact]
    public async Task Poll_does_not_commit_time_cursor_when_order_pages_repeat_a_cursor()
    {
        var server = AddServer(request => Task.FromResult(Feed(
            new[] { OrderJson("REPEAT-1") }, hasMore: true, nextOrderCursor: "same-cursor")));
        var cursorStore = new InMemoryFeedCursorStore();
        var baseline = DateTime.UtcNow.AddMinutes(-2);
        cursorStore.Save(new FeedCursor
        {
            LastPollTime = baseline,
            ProcessedOrders = new Dictionary<string, DateTime>(),
        });
        var service = CreateFeed(server, cursorStore);
        service.OrderReceived += (_, args) =>
        {
            if (args.Order is not null)
                service.ConfirmOrderHandled(args.Order.OrderNumber);
        };

        await service.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => server.Requests.Count >= 2));
        await service.StopListeningAsync();

        Assert.Equal(baseline, cursorStore.Load().LastPollTime);
        Assert.Null(service.LastSuccessfulPollAt);
        Assert.Equal(new string?[] { null, "same-cursor" }, server.Requests.Take(2).Select(request => request.OrderCursor));
    }

    [Fact]
    public async Task Poll_commits_request_start_boundary_so_orders_created_during_response_are_not_lost()
    {
        DateTime? orderCreatedAt = null;
        var server = AddServer(async request =>
        {
            if (orderCreatedAt is null)
            {
                // The request has already started in the client. Create an order just after this
                // server-side observation, hold the response open, and omit the order from the
                // snapshot. A completion-time cursor would move past it permanently.
                orderCreatedAt = DateTime.UtcNow.AddMilliseconds(1);
                await Task.Delay(100);
                return Feed(Array.Empty<string>(), hasMore: false);
            }

            var include = request.ModifiedSince is { } since && orderCreatedAt > since;
            return Feed(include ? new[] { OrderJson("RACE-1", orderCreatedAt.Value) } : Array.Empty<string>(), hasMore: false);
        });
        var cursorStore = new InMemoryFeedCursorStore();
        cursorStore.Save(new FeedCursor
        {
            LastPollTime = DateTime.UtcNow.AddMinutes(-2),
            ProcessedOrders = new Dictionary<string, DateTime>(),
        });
        var service = CreateFeed(server, cursorStore);
        var received = new ConcurrentQueue<string>();
        service.OrderReceived += (_, args) =>
        {
            if (args.Order is not null)
            {
                received.Enqueue(args.Order.OrderNumber);
                service.ConfirmOrderHandled(args.Order.OrderNumber);
            }
        };

        await service.StartListeningAsync();
        Assert.True(await WaitUntilAsync(() => received.Contains("RACE-1")), "an order created during the first response was skipped");
        await service.StopListeningAsync();

        Assert.Equal(new[] { "RACE-1" }, received.ToArray());
        Assert.True(server.Requests.Count >= 2);
        Assert.True(server.Requests.Skip(1).First().ModifiedSince < orderCreatedAt);
    }

    private EventStreamingService CreateFeed(FeedServer server, InMemoryFeedCursorStore cursorStore) =>
        new(
            new FeedPrinter(server.BaseUrl), new NoopRequestLogService(), cursorStore,
            NullLogger<EventStreamingService>.Instance, TimeSpan.FromMilliseconds(20));

    private FeedServer AddServer(Func<FeedRequest, Task<string>> responder)
    {
        var server = new FeedServer(responder);
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

    private static string OrderJson(int number) => OrderJson($"PAGE-{number:00}", DateTime.UtcNow.AddMinutes(-1));

    private static string OrderJson(string orderNumber, DateTime? orderDate = null) => JsonSerializer.Serialize(new
    {
        id = Guid.NewGuid(),
        orderNumber,
        status = "Confirmed",
        type = "TakeAway",
        orderDate = (orderDate ?? DateTime.UtcNow.AddMinutes(-1)).ToUniversalTime(),
        total = 5m,
        items = new[] { new { productName = "Test item", quantity = 1, unitPrice = 5m, itemTotal = 5m } },
    });

    private static string Feed(IEnumerable<string> orders, bool hasMore, string? nextOrderCursor = null)
    {
        var items = orders.Select(order =>
        {
            using var document = JsonDocument.Parse(order);
            return document.RootElement.Clone();
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                items,
                hasMoreOrders = hasMore,
                nextOrderCursor,
                updates = Array.Empty<object>(),
            },
        });
    }

    private sealed class FeedPrinter : IPrinterService
    {
        private readonly string _baseUrl;
        public FeedPrinter(string baseUrl) => _baseUrl = baseUrl;
        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) => Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) => Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(new PrinterConfiguration
        {
            ApiBaseUrl = _baseUrl,
            PrintLanguage = "en",
        });
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }

    private sealed class FeedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<FeedRequest, Task<string>> _responder;
        private readonly ConcurrentQueue<FeedRequest> _requests = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serveTask;

        public FeedServer(Func<FeedRequest, Task<string>> responder)
        {
            _responder = responder;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _serveTask = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }
        public IReadOnlyList<FeedRequest> Requests => _requests.ToArray();

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _serveTask.GetAwaiter().GetResult(); } catch { }
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                var request = new FeedRequest(
                    ParseDateTime(ReadQuery(context.Request.Url, "modifiedSince")),
                    ReadQuery(context.Request.Url, "updateCursor"),
                    ReadQuery(context.Request.Url, "orderCursor"));
                _requests.Enqueue(request);
                try
                {
                    var body = Encoding.UTF8.GetBytes(await _responder(request));
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, _stop.Token);
                }
                catch (OperationCanceledException) { }
                catch (HttpListenerException) { }
                finally
                {
                    try { context.Response.Close(); } catch { }
                }
            }
        }

        private static DateTime? ParseDateTime(string? value) => DateTime.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : null;

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

    private sealed record FeedRequest(DateTime? ModifiedSince, string? UpdateCursor, string? OrderCursor);
}
