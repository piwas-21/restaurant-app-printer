using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class PrinterUpdatePipelineTests
{
    [Theory]
    [InlineData(KitchenPrintStatus.Failed, PrintUpdateJobState.Failed, DevicePrintStatus.Failed)]
    [InlineData(KitchenPrintStatus.NotConfigured, PrintUpdateJobState.NotConfigured, DevicePrintStatus.NotConfigured)]
    [InlineData(KitchenPrintStatus.Unknown, PrintUpdateJobState.Unknown, DevicePrintStatus.Unknown)]
    public async Task Non_success_update_outcomes_stay_pending_and_are_acknowledged_explicitly(
        KitchenPrintStatus printStatus,
        PrintUpdateJobState expectedState,
        DevicePrintStatus expectedAckStatus)
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid());
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var pipeline = CreatePipeline(
            feed,
            store,
            outbox,
            new StubUpdatePrinter(_ => new KitchenPrintOutcome(printStatus)));

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() =>
            store.Records.TryGetValue(update.Key, out var record)
            && record.State == expectedState));
        await pipeline.StopAsync();

        Assert.True(store.Records[update.Key].IsPending);
        Assert.Contains(outbox.Acks, ack => ack.Status == DevicePrintStatus.Queued);
        Assert.Contains(outbox.Acks, ack => ack.Status == expectedAckStatus);
        Assert.DoesNotContain(
            outbox.Acks,
            ack => ack.Status is DevicePrintStatus.Sent or DevicePrintStatus.Printed);
    }

    [Fact]
    public async Task Sent_update_is_terminal_and_carries_job_identity_in_the_ack()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid());
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var pipeline = CreatePipeline(
            feed,
            store,
            outbox,
            new StubUpdatePrinter(_ => KitchenPrintOutcome.Sent));

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() =>
            store.Records.TryGetValue(update.Key, out var record)
            && record.State == PrintUpdateJobState.Sent));
        await pipeline.StopAsync();

        Assert.False(store.Records[update.Key].IsPending);
        var ack = Assert.Single(outbox.Acks, candidate => candidate.Status == DevicePrintStatus.Sent);
        Assert.Equal(update.JobId, ack.JobId);
        Assert.Equal(update.Revision, ack.Revision);
        Assert.Equal(DevicePrintJobType.Update, ack.JobType);
    }

    private static OrderPipeline CreatePipeline(
        TestFeed feed,
        RecordingUpdateStore store,
        RecordingOutbox outbox,
        IOrderPrintService printer) => new(
            feed,
            printer,
            new EmptyHistory(),
            outbox,
            new EmptyTelemetryScheduler(),
            new EmptyPrinterService(),
            new EmptyDeviceIdentity(),
            new NoopRequestLogService(),
            NullLogger<OrderPipeline>.Instance,
            store);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private sealed class TestFeed : IEventStreamingService
    {
        public bool IsListening { get; private set; }
        public DateTime? LastSuccessfulPollAt => null;
        public event EventHandler<OrderEvent>? OrderReceived { add { } remove { } }
        public event EventHandler<PrinterFeedUpdate>? UpdateReceived;
        public event EventHandler<string>? ConnectionStatusChanged { add { } remove { } }
        public Task StartListeningAsync(CancellationToken cancellationToken = default)
        {
            IsListening = true;
            return Task.CompletedTask;
        }
        public Task StopListeningAsync()
        {
            IsListening = false;
            return Task.CompletedTask;
        }
        public void ConfirmOrderHandled(string orderNumber) { }
        public void ReleaseOrderForRetry(string orderNumber, DateTime? createdAt = null, DateTime? updatedAt = null) { }
        public void Emit(PrinterFeedUpdate update) => UpdateReceived?.Invoke(this, update);
    }

    private sealed class StubUpdatePrinter : IOrderPrintService
    {
        private readonly Func<PrinterFeedUpdate, KitchenPrintOutcome> _outcome;
        public StubUpdatePrinter(Func<PrinterFeedUpdate, KitchenPrintOutcome> outcome) => _outcome = outcome;
        public Task<KitchenPrintOutcome> PrintUpdateAsync(PrinterFeedUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(_outcome(update));
        public Task<(bool Cashier, KitchenPrintOutcome FrontKitchen, KitchenPrintOutcome BackKitchen, KitchenPrintOutcome GeneralDefault)> PrintOrderToAllPrintersAsync(Order order, bool isManualPrint = false, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent));
        public Task<bool> PrintOrderAsync(Order order, PrinterType printerType, bool isManualPrint = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingUpdateStore : IPrintUpdateJobStore
    {
        public Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> Records { get; } = new();
        public string FilePath => "memory";
        public string? LoadUpdateCursor() => null;
        public bool TryAdvanceUpdateCursor(string? cursor) => true;
        public bool AddOrGet(PrinterFeedUpdate update, out PrintUpdateJobRecord record, out bool shouldDispatch)
        {
            if (Records.TryGetValue(update.Key, out record!))
            {
                shouldDispatch = record.IsPending;
                return true;
            }
            record = new PrintUpdateJobRecord { Update = update, FirstSeenAt = DateTime.UtcNow };
            Records.Add(update.Key, record);
            shouldDispatch = true;
            return true;
        }
        public IReadOnlyList<PrintUpdateJobRecord> GetPending() => Records.Values.Where(r => r.IsPending).ToList();
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
    }

    private sealed class RecordingOutbox : IPrintAckOutbox
    {
        public List<PrintAck> Acks { get; } = new();
        public Task EnqueueAsync(IEnumerable<PrintAck> acks, CancellationToken cancellationToken = default)
        {
            Acks.AddRange(acks);
            return Task.CompletedTask;
        }
        public Task FlushAsync(Func<IReadOnlyList<PrintAck>, Task<bool>> sender, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyHistory : IOrderHistoryService
    {
        public ReadOnlyObservableCollection<OrderHistoryItem> Orders { get; } =
            new(new ObservableCollection<OrderHistoryItem>());
        public event EventHandler<OrderHistoryItem>? OrderAdded { add { } remove { } }
        public void AddOrder(OrderEvent orderEvent) { }
        public void UpdatePrintStatus(string orderId, bool kitchenPrinted, bool cashierPrinted) { }
        public void ClearHistory() { }
        public OrderHistoryItem? GetOrder(string orderId) => null;
    }

    private sealed class EmptyTelemetryScheduler : ITelemetryScheduler
    {
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class EmptyDeviceIdentity : IDeviceIdentityService
    {
        public string DeviceId => "test";
        public string Platform => "test";
        public string AppVersion => "test";
        public void ApplySentryTags(string tenantSlug) { }
    }

    private sealed class EmptyPrinterService : IPrinterService
    {
        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) => Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) => Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(new PrinterConfiguration());
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }
}
