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
    public async Task Unknown_delivery_is_terminal_and_is_not_retried_automatically()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid());
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var printer = new StubUpdatePrinter(_ => KitchenPrintOutcome.Unknown);
        var pipeline = CreatePipeline(feed, store, outbox, printer);

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() =>
            store.Records.TryGetValue(update.Key, out var record)
            && record.State == PrintUpdateJobState.Unknown
            && record.FinalAcknowledgementQueued));
        await pipeline.StopAsync();

        Assert.False(store.Records[update.Key].IsPending);
        Assert.Single(outbox.Acks, ack => ack.Status == DevicePrintStatus.Unknown);
        Assert.Equal(1, printer.UpdatePrintCalls);
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
        Assert.True(store.Records[update.Key].FinalAcknowledgementQueued);
    }

    [Fact]
    public async Task Failed_final_ack_persistence_does_not_reclassify_sent_or_retry_the_print()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid());
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox { FailOnCall = 2 };
        var printer = new StubUpdatePrinter(_ => KitchenPrintOutcome.Sent);
        var pipeline = CreatePipeline(feed, store, outbox, printer);

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() =>
            store.Records.TryGetValue(update.Key, out var record)
            && record.State == PrintUpdateJobState.Sent));
        await pipeline.StopAsync();

        Assert.False(store.Records[update.Key].IsPending);
        Assert.False(store.Records[update.Key].FinalAcknowledgementQueued);
        Assert.Equal(1, printer.UpdatePrintCalls);
        outbox.FailOnCall = null;

        var restarted = CreatePipeline(new TestFeed(), store, outbox, printer);
        await restarted.StartAsync();
        Assert.True(await WaitUntilAsync(() => store.Records[update.Key].FinalAcknowledgementQueued));
        await restarted.StopAsync();

        Assert.Equal(1, printer.UpdatePrintCalls);
        Assert.Contains(outbox.Acks, ack => ack.Status == DevicePrintStatus.Sent);
    }

    [Fact]
    public async Task Missing_authorization_fails_closed_and_leaves_a_retryable_no_send()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid());
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var printer = new StubUpdatePrinter(_ => KitchenPrintOutcome.Sent);
        var pipeline = CreatePipeline(feed, store, outbox, printer,
            new StubAuthorization(_ => PrinterUpdateAuthorizationResult.Unavailable));

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() => store.Records[update.Key].State == PrintUpdateJobState.Failed));
        await pipeline.StopAsync();

        Assert.True(store.Records[update.Key].IsPending);
        Assert.Equal(0, printer.UpdatePrintCalls);
        Assert.Contains(outbox.Acks, ack => ack.Status == DevicePrintStatus.Failed
            && ack.JobId == update.JobId && ack.Revision == update.Revision);
    }

    [Fact]
    public async Task Withdrawn_authorization_redacts_local_payload_and_never_prints()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), text: "customer asked to remove onions") with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.InstructionChange,
                    Previous = new OrderItem
                    {
                        Id = "line-1", ProductName = "Burger", Quantity = 1,
                        SpecialInstructions = "leave off the onions",
                    },
                    Current = new OrderItem
                    {
                        Id = "line-1", ProductName = "Burger", Quantity = 1,
                        SpecialInstructions = "leave off the onions",
                    },
                },
            },
        };
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var printer = new StubUpdatePrinter(_ => KitchenPrintOutcome.Sent);
        var pipeline = CreatePipeline(feed, store, outbox, printer,
            new StubAuthorization(_ => PrinterUpdateAuthorizationResult.Withdrawn));

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() => store.Records[update.Key].State == PrintUpdateJobState.Withdrawn
            && store.Records[update.Key].FinalAcknowledgementQueued));
        await pipeline.StopAsync();

        var saved = store.Records[update.Key].Update;
        Assert.True(saved.IsWithdrawn);
        Assert.Empty(saved.Text);
        Assert.Equal("line-1", saved.Changes[0].Current!.Id);
        Assert.Equal(1, saved.Changes[0].Current!.Quantity);
        Assert.Null(saved.Changes[0].Current!.SpecialInstructions);
        Assert.Equal(0, printer.UpdatePrintCalls);
        Assert.Contains(outbox.Acks, ack => ack.Status == DevicePrintStatus.Skipped
            && ack.FailureReason == "Withdrawn"
            && ack.JobId == update.JobId && ack.Revision == update.Revision);
    }

    [Fact]
    public async Task Revision_two_withdrawal_is_acknowledged_without_authorization_or_output()
    {
        var withdrawal = PrinterUpdateTestData.Update(Guid.NewGuid(), revision: 2, text: "") with
        {
            IsWithdrawn = true,
        };
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(withdrawal, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var printer = new StubUpdatePrinter(_ => KitchenPrintOutcome.Sent);
        var authorization = new StubAuthorization(
            (Func<PrinterFeedUpdate, PrinterUpdateAuthorizationResult>)(_ =>
                throw new InvalidOperationException(
                    "Withdrawal metadata must not call the authorization endpoint.")));
        var pipeline = CreatePipeline(feed, store, outbox, printer, authorization);

        await pipeline.StartAsync();
        feed.Emit(withdrawal);
        Assert.True(await WaitUntilAsync(() => store.Records[withdrawal.Key].State == PrintUpdateJobState.Withdrawn
            && store.Records[withdrawal.Key].FinalAcknowledgementQueued));
        await pipeline.StopAsync();

        Assert.Equal(0, authorization.CallCount);
        Assert.Equal(0, printer.UpdatePrintCalls);
        Assert.Contains(outbox.Acks, ack => ack.Status == DevicePrintStatus.Skipped
            && ack.FailureReason == "Withdrawn"
            && ack.JobId == withdrawal.JobId && ack.Revision == 2);
    }

    [Fact]
    public async Task Local_withdrawal_arriving_during_authorization_blocks_the_preprint_call()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid());
        var withdrawal = update with
        {
            Revision = 2,
            IsWithdrawn = true,
            Text = string.Empty,
            Changes = Array.Empty<PrinterFeedChange>(),
            CreatedAt = DateTime.UtcNow.AddSeconds(1),
        };
        var store = new RecordingUpdateStore();
        Assert.True(store.AddOrGet(update, out _, out _));
        var feed = new TestFeed();
        var outbox = new RecordingOutbox();
        var printer = new StubUpdatePrinter(_ => KitchenPrintOutcome.Sent);
        var authorizationCompleted = new TaskCompletionSource<PrinterUpdateAuthorizationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = new StubAuthorization(_ => authorizationCompleted.Task);
        var pipeline = CreatePipeline(feed, store, outbox, printer, authorization);

        await pipeline.StartAsync();
        feed.Emit(update);
        Assert.True(await WaitUntilAsync(() => authorization.CallCount == 1));
        Assert.True(store.AddOrGet(withdrawal, out _, out _));
        authorizationCompleted.SetResult(PrinterUpdateAuthorizationResult.Authorized);
        Assert.True(await WaitUntilAsync(() => store.Records[update.Key].State == PrintUpdateJobState.Withdrawn));
        await pipeline.StopAsync();

        Assert.Equal(0, printer.UpdatePrintCalls);
        Assert.Empty(store.Records[update.Key].Update.Text);
        Assert.True(store.Records[update.Key].Update.IsWithdrawn);
    }

    private static OrderPipeline CreatePipeline(
        TestFeed feed,
        RecordingUpdateStore store,
        RecordingOutbox outbox,
        IOrderPrintService printer,
        IPrinterUpdateAuthorizationService? authorization = null) => new(
            feed,
            printer,
            new EmptyHistory(),
            outbox,
            new EmptyTelemetryScheduler(),
            new EmptyPrinterService(),
            new EmptyDeviceIdentity(),
            new NoopRequestLogService(),
            NullLogger<OrderPipeline>.Instance,
            store,
            authorization ?? new StubAuthorization(_ => PrinterUpdateAuthorizationResult.Authorized));

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
        public int UpdatePrintCalls { get; private set; }
        public StubUpdatePrinter(Func<PrinterFeedUpdate, KitchenPrintOutcome> outcome) => _outcome = outcome;
        public async Task<KitchenPrintOutcome> PrintUpdateAsync(
            PrinterFeedUpdate update,
            Func<CancellationToken, Task<PrinterUpdateAuthorizationResult>> authorizeImmediatelyBeforeSend,
            CancellationToken cancellationToken = default)
        {
            var authorization = await authorizeImmediatelyBeforeSend(cancellationToken);
            if (authorization.Status == PrinterUpdateAuthorizationStatus.Withdrawn)
                return KitchenPrintOutcome.Skipped;
            if (authorization.Status != PrinterUpdateAuthorizationStatus.Authorized)
                return KitchenPrintOutcome.Failed;
            UpdatePrintCalls++;
            return _outcome(update);
        }
        public Task<KitchenPrintOutcome> PrintUpdateCopyAsync(PrinterFeedUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(KitchenPrintOutcome.Sent);
        public Task<(bool Cashier, KitchenPrintOutcome FrontKitchen, KitchenPrintOutcome BackKitchen, KitchenPrintOutcome GeneralDefault)> PrintOrderToAllPrintersAsync(Order order, bool isManualPrint = false, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent));
        public Task<bool> PrintOrderAsync(Order order, PrinterType printerType, bool isManualPrint = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubAuthorization(
        Func<PrinterFeedUpdate, Task<PrinterUpdateAuthorizationResult>> result) : IPrinterUpdateAuthorizationService
    {
        public int CallCount { get; private set; }

        public StubAuthorization(Func<PrinterFeedUpdate, PrinterUpdateAuthorizationResult> result)
            : this(update => Task.FromResult(result(update)))
        {
        }

        public Task<PrinterUpdateAuthorizationResult> CheckAsync(
            PrinterFeedUpdate update, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return result(update);
        }
    }

    private sealed class RecordingUpdateStore : IPrintUpdateJobStore
    {
        public Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> Records { get; } = new();
        public string FilePath => "memory";
        public string? LoadUpdateCursor() => null;
        public bool TryAdvanceUpdateCursor(string? cursor) => true;
        public bool AddOrGet(PrinterFeedUpdate update, out PrintUpdateJobRecord record, out bool shouldDispatch)
        {
            if (!update.IsWithdrawn && Records.TryGetValue(
                new PrintUpdateJobKey(update.JobId, 2, update.Target), out var withdrawal))
            {
                record = withdrawal;
                shouldDispatch = false;
                return true;
            }
            if (Records.TryGetValue(update.Key, out record!))
            {
                if (record.Update.IsWithdrawn && update.Revision == 1)
                {
                    shouldDispatch = false;
                    return true;
                }
                shouldDispatch = record.IsPending;
                return true;
            }
            if (update.IsWithdrawn && Records.TryGetValue(
                new PrintUpdateJobKey(update.JobId, 1, update.Target), out var original))
            {
                var originalState = original.State is PrintUpdateJobState.Pending
                    or PrintUpdateJobState.Failed or PrintUpdateJobState.NotConfigured
                    ? PrintUpdateJobState.Withdrawn : original.State;
                Records[original.Key] = original with
                {
                    Update = PrinterUpdateWithdrawalRedactor.Redact(original.Update),
                    State = originalState,
                    FinalAcknowledgementQueued = originalState == PrintUpdateJobState.Withdrawn
                        ? false : original.FinalAcknowledgementQueued,
                };
            }
            record = new PrintUpdateJobRecord { Update = update, FirstSeenAt = DateTime.UtcNow };
            Records.Add(update.Key, record);
            shouldDispatch = true;
            return true;
        }
        public IReadOnlyList<PrintUpdateJobRecord> GetPending() => Records.Values.Where(r => r.IsPending).ToList();
        public IReadOnlyList<PrintUpdateJobRecord> GetPendingFinalAcknowledgements() => Records.Values
            .Where(record => !record.IsPending && record.State != PrintUpdateJobState.Processing
                && !record.FinalAcknowledgementQueued)
            .ToList();
        public bool TryBegin(PrintUpdateJobKey key)
        {
            if (!Records.TryGetValue(key, out var record) || !record.IsPending)
                return false;
            Records[key] = record with { State = PrintUpdateJobState.Processing, FinalAcknowledgementQueued = false };
            return true;
        }
        public bool MarkWithdrawn(PrintUpdateJobKey key)
        {
            if (!Records.TryGetValue(key, out var record))
                return false;
            var state = record.IsPending ? PrintUpdateJobState.Withdrawn : record.State;
            Records[key] = record with
            {
                Update = PrinterUpdateWithdrawalRedactor.Redact(record.Update),
                State = state,
                FailureReason = state == PrintUpdateJobState.Withdrawn ? "Withdrawn by the backend." : record.FailureReason,
                FinalAcknowledgementQueued = state == PrintUpdateJobState.Withdrawn
                    ? false : record.FinalAcknowledgementQueued,
            };
            return true;
        }
        public bool IsWithdrawalRequested(PrintUpdateJobKey key) =>
            Records.TryGetValue(key, out var record) && record.Update.IsWithdrawn;
        public bool Complete(PrintUpdateJobKey key, PrintUpdateJobState state, string? failureReason = null)
        {
            if (!Records.TryGetValue(key, out var record))
                return false;
            Records[key] = record with { State = state, FailureReason = failureReason, FinalAcknowledgementQueued = false };
            return true;
        }
        public bool MarkFinalAcknowledgementQueued(PrintUpdateJobKey key)
        {
            if (!Records.TryGetValue(key, out var record))
                return false;
            Records[key] = record with { FinalAcknowledgementQueued = true };
            return true;
        }
        public IReadOnlyList<PrintUpdateJobRecord> GetHistory() => Records.Values.ToList();
    }

    private sealed class RecordingOutbox : IPrintAckOutbox
    {
        public List<PrintAck> Acks { get; } = new();
        public int? FailOnCall { get; set; }
        private int _callCount;
        public Task EnqueueAsync(IEnumerable<PrintAck> acks, CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == FailOnCall)
                throw new IOException("simulated outbox write failure");
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
