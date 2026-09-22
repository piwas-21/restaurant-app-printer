using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class EventStreamingServicePrintRecoveryTests
{
    [Fact]
    public async Task Sse_no_work_releases_dedup_and_later_assigned_poll_prints_once()
    {
        var cursor = new RecordingCursorStore();
        var feed = CreateFeed(cursor);
        var printer = new RecoveryPrintService();
        var pipeline = CreatePipeline(feed, printer);
        await pipeline.StartAsync();

        var first = RoutedOrderJson("SSE-RECOVER-NOWORK", "other-device");
        await feed.ProcessEventAsync("order-created", first, "kitchen", CancellationToken.None);
        await printer.WaitForCallsAsync(1);

        var second = RoutedOrderJson("SSE-RECOVER-NOWORK", "device-a");
        await feed.ProcessEventAsync("order-updated", second, "poll", CancellationToken.None);
        await printer.WaitForCallsAsync(2);
        await WaitUntilAsync(() => cursor.Current.ProcessedOrders.ContainsKey("SSE-RECOVER-NOWORK"));

        Assert.Equal(2, printer.PrintCalls);
        Assert.Equal(1, printer.SuccessfulPrints);
        Assert.Contains("SSE-RECOVER-NOWORK", cursor.Current.ProcessedOrders.Keys);
    }

    [Fact]
    public async Task Sse_print_failure_releases_dedup_and_later_poll_recovers()
    {
        var cursor = new RecordingCursorStore();
        var feed = CreateFeed(cursor);
        var printer = new RecoveryPrintService { FailFirstPrint = true };
        var pipeline = CreatePipeline(feed, printer);
        await pipeline.StartAsync();

        var payload = RoutedOrderJson("SSE-RECOVER-FAILURE", "device-a");
        await feed.ProcessEventAsync("order-created", payload, "kitchen", CancellationToken.None);
        await printer.WaitForCallsAsync(1);

        await feed.ProcessEventAsync("order-updated", payload, "poll", CancellationToken.None);
        await printer.WaitForCallsAsync(2);
        await WaitUntilAsync(() => cursor.Current.ProcessedOrders.ContainsKey("SSE-RECOVER-FAILURE"));

        Assert.Equal(2, printer.PrintCalls);
        Assert.Equal(1, printer.SuccessfulPrints);
        Assert.Contains("SSE-RECOVER-FAILURE", cursor.Current.ProcessedOrders.Keys);
    }

    [Fact]
    public async Task Successful_sse_remains_deduplicated()
    {
        var feed = CreateFeed();
        var printer = new RecoveryPrintService();
        var pipeline = CreatePipeline(feed, printer);
        await pipeline.StartAsync();

        var payload = RoutedOrderJson("SSE-DEDUP-SUCCESS", "device-a");
        await feed.ProcessEventAsync("order-created", payload, "kitchen", CancellationToken.None);
        await printer.WaitForCallsAsync(1);

        await feed.ProcessEventAsync("order-updated", payload, "poll", CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, printer.PrintCalls);
        Assert.Equal(1, printer.SuccessfulPrints);
    }

    private static EventStreamingService CreateFeed(IFeedCursorStore? cursorStore = null) => new(
        new FeedPrinterService(), new NoopRequestLogService(), cursorStore ?? new RecordingCursorStore(),
        NullLogger<EventStreamingService>.Instance);

    private static OrderPipeline CreatePipeline(
        EventStreamingService feed,
        RecoveryPrintService printer)
    {
        var config = new PrinterConfiguration();
        return new OrderPipeline(
            feed,
            printer,
            new RecoveryHistory(),
            new RecoveryOutbox(),
            new RecoveryTelemetryScheduler(),
            new FeedPrinterService(config),
            new RecoveryDeviceIdentity(),
            new NoopRequestLogService(),
            NullLogger<OrderPipeline>.Instance);
    }

    private static string RoutedOrderJson(string orderNumber, string deviceId) => $$"""
        {
          "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "orderNumber": "{{orderNumber}}",
          "status": "Confirmed",
          "routingStates": [
            {
              "jobId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "revision": 1,
              "target": "General",
              "status": "Queued",
              "deviceId": "{{deviceId}}"
            }
          ],
          "items": [{ "productName": "Pide", "quantity": 1 }]
        }
        """;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition(), "The asynchronous print recovery did not reach the expected state.");
    }

    private sealed class RecoveryPrintService : IOrderPrintService
    {
        public int PrintCalls { get; private set; }
        public int SuccessfulPrints { get; private set; }
        public bool FailFirstPrint { get; init; }

        public Task<(bool Cashier, KitchenPrintOutcome FrontKitchen, KitchenPrintOutcome BackKitchen, KitchenPrintOutcome GeneralDefault)>
            PrintOrderToAllPrintersAsync(
                Order order,
                bool isManualPrint = false,
                CancellationToken cancellationToken = default)
        {
            PrintCalls++;
            var localWork = order.RoutingStates?.Any(route =>
                route.Status == DevicePrintStatus.Queued && route.DeviceId == "device-a") == true;
            var successful = localWork && (!FailFirstPrint || PrintCalls > 1);
            if (successful)
                SuccessfulPrints++;
            return Task.FromResult((
                Cashier: successful,
                FrontKitchen: successful ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                BackKitchen: successful ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                GeneralDefault: localWork
                    ? successful ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed
                    : KitchenPrintOutcome.NoWork));
        }

        public Task<bool> PrintOrderAsync(
            Order order,
            PrinterType printerType,
            bool isManualPrint = false,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<KitchenPrintOutcome> PrintUpdateAsync(
            PrinterFeedUpdate update,
            CancellationToken cancellationToken = default) => Task.FromResult(KitchenPrintOutcome.Sent);

        public async Task WaitForCallsAsync(int expected)
        {
            for (var attempt = 0; attempt < 500 && PrintCalls < expected; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(PrintCalls >= expected, $"Expected {expected} print calls, got {PrintCalls}.");
        }
    }

    private sealed class FeedPrinterService : IPrinterService
    {
        private readonly PrinterConfiguration _config;

        public FeedPrinterService(PrinterConfiguration? config = null) =>
            _config = config ?? new PrinterConfiguration();

        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) =>
            Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
            Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(_config);
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }

    private sealed class RecordingCursorStore : IFeedCursorStore
    {
        private FeedCursor _cursor = new()
        {
            LastPollTime = DateTime.UtcNow - FeedCursorStore.MaxLookBack,
        };

        public FeedCursor Current => Load();

        public FeedCursor Load() => new()
        {
            LastPollTime = _cursor.LastPollTime,
            ProcessedOrders = new Dictionary<string, DateTime>(_cursor.ProcessedOrders),
            LastUpdateCursor = _cursor.LastUpdateCursor,
        };

        public void Save(FeedCursor cursor) => _cursor = new()
        {
            LastPollTime = cursor.LastPollTime,
            ProcessedOrders = new Dictionary<string, DateTime>(cursor.ProcessedOrders),
            LastUpdateCursor = cursor.LastUpdateCursor,
        };
    }

    private sealed class RecoveryDeviceIdentity : IDeviceIdentityService
    {
        public string DeviceId => "device-a";
        public string Platform => "Test";
        public string AppVersion => "test";
        public void ApplySentryTags(string tenantSlug) { }
    }

    private sealed class RecoveryHistory : IOrderHistoryService
    {
        public ReadOnlyObservableCollection<OrderHistoryItem> Orders { get; } =
            new(new ObservableCollection<OrderHistoryItem>());
        public event EventHandler<OrderHistoryItem>? OrderAdded { add { } remove { } }
        public void AddOrder(OrderEvent orderEvent) { }
        public void UpdatePrintStatus(string orderId, bool kitchenPrinted, bool cashierPrinted) { }
        public void ClearHistory() { }
        public OrderHistoryItem? GetOrder(string orderId) => null;
    }

    private sealed class RecoveryOutbox : IPrintAckOutbox
    {
        public Task EnqueueAsync(IEnumerable<PrintAck> acks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task FlushAsync(Func<IReadOnlyList<PrintAck>, Task<bool>> sender,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecoveryTelemetryScheduler : ITelemetryScheduler
    {
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
    }
}
