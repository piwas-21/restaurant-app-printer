using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Issue #113 S2: pipeline-level print outcomes. The REAL <see cref="OrderPipeline"/> drives the
/// REAL <see cref="OrderPrintService"/> (compose → transport) into loopback sinks — the filter
/// tests in <see cref="KitchenRoutingPolicyTests"/> prove what a ticket CONTAINS, this class proves
/// what the order path PRINTS and REPORTS:
/// <list type="bullet">
/// <item>all-unassigned work in SingleKitchen mode prints exactly ONE General ticket;</item>
/// <item>unassigned work in Stations mode with no resolvable destination reports
/// <see cref="KitchenPrintStatus.NotConfigured"/> and is not success — not on the event args, not
/// in history, nowhere;</item>
/// <item>mixed Front/Back work in Stations mode behaves byte-for-byte as before.</item>
/// </list>
/// Feed, history, outbox and telemetry are stubs; the pipeline's own print/bookkeeping sequencing
/// (history update, ack outbox, ConfirmOrderHandled, OrderProcessed) runs for real.
/// </summary>
public sealed class OrderPipelinePrintOutcomeTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly Sink _cashier = new();
    private readonly Sink _front = new();
    private readonly Sink _back = new();
    private readonly Sink _generalDefault = new();
    private readonly FakeAppDataPathProvider _paths = new();
    private readonly RecordingHistory _history = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly StubFeed _feed = new();
    private readonly RecordingRequestLog _requestLog = new();

    public void Dispose()
    {
        _cashier.Dispose();
        _front.Dispose();
        _back.Dispose();
        _generalDefault.Dispose();
        _paths.Dispose();
    }

    /// <summary>SingleKitchen policy, cashier + one explicit General destination, no stations.</summary>
    private PrinterConfiguration SingleKitchenConfig() => new()
    {
        KitchenRoutingMode = KitchenRoutingMode.SingleKitchen,
        CashierPrinterName = _cashier.PrinterName,
        CashierAutoPrint = true,
        DefaultKitchenPrinterName = _generalDefault.PrinterName,
    };

    [Fact]
    public async Task SingleKitchen_AllUnassignedOrder_PrintsExactlyOneGeneralTicket()
    {
        var order = new Order
        {
            Id = "9a1b6bd1-0a5b-4a0e-9d2a-3f3c1c0a4e11",
            OrderNumber = "PIPE-A",
            Type = "DineIn",
            TableNumber = 2,
            Status = "Confirmed",
            Total = 30.00m,
            OrderDate = DateTime.Now,
            Items =
            {
                // Every line unassigned (null / "None"), nested three deep, quantity > 1 — the
                // whole tree belongs on the one General ticket, each line exactly once.
                new OrderItem
                {
                    ProductName = "Adana Kebab", Quantity = 2, KitchenType = null,
                    SideItems =
                    [
                        new OrderItem
                        {
                            ProductName = "Ayran", Quantity = 1, KitchenType = "None",
                            SideItems = [new OrderItem { ProductName = "Extra glass", Quantity = 1 }],
                        },
                    ],
                },
                new OrderItem { ProductName = "Pide", Quantity = 1, KitchenType = "None" },
            },
        };

        using var cts = new CancellationTokenSource(TestTimeout);
        var args = await RunAsync(SingleKitchenConfig(), order, cts.Token);

        // Every target reports success, including the General destination actually SENT.
        Assert.True(args.Cashier);
        Assert.True(args.FrontKitchen);
        Assert.True(args.BackKitchen);
        Assert.Equal(KitchenPrintStatus.Sent, args.GeneralDefault.Status);
        Assert.True(args.AllPrinted);

        var ticket = await _generalDefault.ReadTicketAsync(cts.Token);
        Assert.Contains("General Kitchen", ticket);
        Assert.Equal(1, Occurrences(ticket, "Adana Kebab"));
        Assert.Equal(1, Occurrences(ticket, "Ayran"));
        Assert.Equal(1, Occurrences(ticket, "Extra glass"));
        Assert.Equal(1, Occurrences(ticket, "Pide"));
        Assert.Contains("2x Adana Kebab", ticket);

        // Exactly one kitchen ticket went out: no station printer is configured in this mode, and
        // none was contacted.
        Assert.False(_front.ReceivedAnything, "Stations mode owes Front nothing, and neither does SingleKitchen");
        Assert.False(_back.ReceivedAnything, "SingleKitchen must not send station tickets");
        Assert.True(_cashier.ReceivedAnything, "the cashier receipt is unconditional");

        // Session dedup still confirms the order (it went through the printers).
        Assert.Contains(order.OrderNumber, _feed.Confirmed);
    }

    [Fact]
    public async Task RoutedOrderWithoutLocalQueuedWork_IsNotConfirmedOrRecordedAsPrinted()
    {
        var order = new Order
        {
            Id = "9a1b6bd1-0a5b-4a0e-9d2a-3f3c1c0a4e99",
            OrderNumber = "PIPE-ROUTE-NONE",
            Type = "TakeAway",
            Status = "Confirmed",
            Total = 18.00m,
            OrderDate = DateTime.Now,
            Items = { new OrderItem { ProductName = "Soup", Quantity = 1, KitchenType = "None" } },
            RoutingStates =
            [new()
            {
                JobId = Guid.NewGuid(), Revision = 1, Target = DevicePrintTarget.General,
                DeviceId = "other-device", Status = DevicePrintStatus.Queued,
            }],
        };

        using var cts = new CancellationTokenSource(TestTimeout);
        var args = await RunAsync(SingleKitchenConfig(), order, cts.Token);

        Assert.Equal(KitchenPrintStatus.NoWork, args.GeneralDefault.Status);
        Assert.False(args.Cashier);
        Assert.DoesNotContain(order.OrderNumber, _feed.Confirmed);
        Assert.Empty(_outbox.Enqueued);
        Assert.False(_cashier.ReceivedAnything);
        Assert.False(_generalDefault.ReceivedAnything);
    }

    [Fact]
    public async Task RoutedOrder_PersistsFinalAckBeforeConfirmingFeed()
    {
        var jobId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid().ToString(),
            OrderNumber = "PIPE-ROUTE-ACK",
            Type = "TakeAway",
            Status = "Confirmed",
            Total = 18.00m,
            OrderDate = DateTime.UtcNow,
            Items = { new OrderItem { ProductName = "Soup", Quantity = 1, KitchenType = "None" } },
            RoutingStates =
            [new()
            {
                JobId = jobId, Revision = 7, Target = DevicePrintTarget.General,
                DeviceId = "test-device", Status = DevicePrintStatus.Queued,
            }],
        };
        _outbox.DuringEnqueue = () => Assert.Empty(_feed.Confirmed);

        using var cts = new CancellationTokenSource(TestTimeout);
        await RunAsync(SingleKitchenConfig(), order, cts.Token);

        var ack = Assert.Single(_outbox.Enqueued);
        Assert.Equal(jobId, ack.JobId);
        Assert.Equal(7, ack.Revision);
        Assert.Equal(DevicePrintJobType.Order, ack.JobType);
        Assert.Contains(order.OrderNumber, _feed.Confirmed);
    }

    [Fact]
    public async Task RoutedOrder_AckPersistenceFailureLeavesPhysicalPrintAmbiguousAndUnconfirmed()
    {
        var order = new Order
        {
            Id = Guid.NewGuid().ToString(),
            OrderNumber = "PIPE-ROUTE-ACK-FAIL",
            Type = "TakeAway",
            Status = "Confirmed",
            Total = 18.00m,
            OrderDate = DateTime.UtcNow,
            Items = { new OrderItem { ProductName = "Soup", Quantity = 1, KitchenType = "None" } },
            RoutingStates =
            [new()
            {
                JobId = Guid.NewGuid(), Revision = 2, Target = DevicePrintTarget.General,
                DeviceId = "test-device", Status = DevicePrintStatus.Queued,
            }],
        };
        _outbox.EnqueueFailure = new IOException("disk full");

        using var cts = new CancellationTokenSource(TestTimeout);
        var args = await RunAsync(SingleKitchenConfig(), order, cts.Token);

        Assert.True(_generalDefault.ReceivedAnything, "physical output should have happened before ack failure");
        Assert.Empty(_feed.Confirmed);
        Assert.Contains(order.OrderNumber, _feed.Released);
        Assert.True(args.AckPersistenceAmbiguous);
        Assert.Contains(_requestLog.Errors, error =>
            error.Contains("duplicate paper", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)] // nothing kitchen-side is configured at all
    [InlineData(true)]  // both stations configured — the resolver must not guess between them
    public async Task Stations_UnassignedWorkWithNoResolvableDestination_ReportsNotConfigured_NotSuccess(
        bool bothStationsConfigured)
    {
        var config = new PrinterConfiguration
        {
            KitchenRoutingMode = KitchenRoutingMode.Stations,
            CashierPrinterName = _cashier.PrinterName,
            CashierAutoPrint = true,
            // DefaultKitchenPrinterName and KitchenPrinterName stay empty.
        };
        if (bothStationsConfigured)
        {
            config.FrontKitchenPrinterName = _front.PrinterName;
            config.BackKitchenPrinterName = _back.PrinterName;
        }

        var order = new Order
        {
            Id = "9a1b6bd1-0a5b-4a0e-9d2a-3f3c1c0a4e12",
            OrderNumber = "PIPE-B",
            Type = "TakeAway",
            Status = "Confirmed",
            Total = 18.00m,
            OrderDate = DateTime.Now,
            Items =
            {
                new OrderItem
                {
                    ProductName = "Lahmacun", Quantity = 2, KitchenType = null,
                    SideItems = [new OrderItem { ProductName = "Lemon", Quantity = 1, KitchenType = "None" }],
                },
            },
        };

        using var cts = new CancellationTokenSource(TestTimeout);
        var args = await RunAsync(config, order, cts.Token);

        // THE regression: work exists, no kitchen ticket can come out, and the path must NOT
        // report success.
        Assert.Equal(KitchenPrintStatus.NotConfigured, args.GeneralDefault.Status);
        Assert.False(args.GeneralDefault.IsSuccess);
        Assert.False(args.AllPrinted);
        Assert.True(args.Cashier, "the cashier receipt is unconditional");

        // Nothing was booked as printed.
        var update = Assert.Single(_history.Updates);
        Assert.False(update.KitchenPrinted);
        Assert.True(update.CashierPrinted);

        // And no kitchen printer was contacted — there is no resolvable destination.
        Assert.False(_generalDefault.ReceivedAnything);
        Assert.False(_front.ReceivedAnything);
        Assert.False(_back.ReceivedAnything);

        // The fleet ack still goes out with the real per-target statuses; the cashier's is Printed
        // and the NotConfigured leg fabricates no extra ack (General/Default has no ack target yet —
        // the known PrintAcks limitation stays unchanged here).
        Assert.Equal(3, _outbox.Enqueued.Count);
        var cashierAck = Assert.Single(_outbox.Enqueued, ack => ack.Target == DevicePrintTarget.Cashier);
        Assert.Equal(DevicePrintStatus.Printed, cashierAck.Status);

        // Session dedup still confirms the order (it went through the printers; the destination
        // stays pending configuration, which is surfaced by the outcome, not by re-driving).
        Assert.Contains(order.OrderNumber, _feed.Confirmed);
    }

    [Fact]
    public async Task Stations_UnassignedWork_GoesToTheResolvedDefaultDestination()
    {
        // Legacy fallback leg of the resolver: no Default printer, so the legacy kitchen printer
        // carries the Default ticket.
        var config = new PrinterConfiguration
        {
            KitchenRoutingMode = KitchenRoutingMode.Stations,
            CashierPrinterName = _cashier.PrinterName,
            CashierAutoPrint = true,
            KitchenPrinterName = _generalDefault.PrinterName,
        };

        var order = new Order
        {
            Id = "9a1b6bd1-0a5b-4a0e-9d2a-3f3c1c0a4e13",
            OrderNumber = "PIPE-D",
            Type = "DineIn",
            TableNumber = 6,
            Status = "Confirmed",
            Total = 24.00m,
            OrderDate = DateTime.Now,
            Items =
            {
                new OrderItem
                {
                    ProductName = "Iskender", Quantity = 2, KitchenType = "None",
                    SideItems = [new OrderItem { ProductName = "Butter", Quantity = 1, KitchenType = null }],
                },
            },
        };

        using var cts = new CancellationTokenSource(TestTimeout);
        var args = await RunAsync(config, order, cts.Token);

        Assert.Equal(KitchenPrintStatus.Sent, args.GeneralDefault.Status);
        Assert.True(args.AllPrinted);

        var ticket = await _generalDefault.ReadTicketAsync(cts.Token);
        Assert.Contains("Default Kitchen", ticket);
        Assert.Equal(1, Occurrences(ticket, "Iskender"));
        Assert.Equal(1, Occurrences(ticket, "Butter"));
        Assert.Contains("2x Iskender", ticket);
        Assert.False(_front.ReceivedAnything);
        Assert.False(_back.ReceivedAnything);
    }

    [Fact]
    public async Task Stations_MixedFrontBackOrder_BehavesExactlyAsBefore()
    {
        var config = new PrinterConfiguration
        {
            KitchenRoutingMode = KitchenRoutingMode.Stations,
            CashierPrinterName = _cashier.PrinterName,
            CashierAutoPrint = true,
            FrontKitchenPrinterName = _front.PrinterName,
            FrontKitchenAutoPrint = true,
            BackKitchenPrinterName = _back.PrinterName,
            BackKitchenAutoPrint = true,
            // No Default/legacy printer: none is needed — this order owes no Default work, so the
            // Default destination must not even be contacted.
        };

        var order = new Order
        {
            Id = "9a1b6bd1-0a5b-4a0e-9d2a-3f3c1c0a4e14",
            OrderNumber = "PIPE-C",
            Type = "DineIn",
            TableNumber = 3,
            Status = "Confirmed",
            OrderDate = DateTime.Now,
            Items =
            {
                new OrderItem
                {
                    ProductName = "Menu Deal", Quantity = 1, KitchenType = "FrontKitchen",
                    SideItems =
                    [
                        new OrderItem { ProductName = "Kofte", Quantity = 1, KitchenType = "FrontKitchen" },
                        new OrderItem { ProductName = "Ayran", Quantity = 1, KitchenType = null }, // rides with Front
                        new OrderItem { ProductName = "Fries", Quantity = 2, KitchenType = "BackKitchen" },
                    ],
                },
            },
        };

        using var cts = new CancellationTokenSource(TestTimeout);
        var args = await RunAsync(config, order, cts.Token);

        Assert.True(args.FrontKitchen);
        Assert.True(args.BackKitchen);
        // No unassigned work → the General/Default destination owes nothing → trivially satisfied,
        // which is what kept AllPrinted true before this change.
        Assert.Equal(KitchenPrintStatus.Sent, args.GeneralDefault.Status);
        Assert.True(args.AllPrinted);

        var frontTicket = await _front.ReadTicketAsync(cts.Token);
        Assert.Equal(1, Occurrences(frontTicket, "Menu Deal"));
        Assert.Equal(1, Occurrences(frontTicket, "Kofte"));
        Assert.Equal(1, Occurrences(frontTicket, "Ayran"));
        Assert.Equal(0, Occurrences(frontTicket, "Fries")); // the historic bug: fries rode along here

        var backTicket = await _back.ReadTicketAsync(cts.Token);
        Assert.Contains("2x Fries", backTicket);
        Assert.Equal(0, Occurrences(backTicket, "Kofte"));
        Assert.Contains("(1x Menu Deal)", backTicket); // the combo rides along as context only

        Assert.False(_generalDefault.ReceivedAnything, "no unassigned work → no Default ticket, no contact");
        Assert.True(_cashier.ReceivedAnything);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the pipeline with the real print service and raises one order through the feed.</summary>
    private async Task<OrderProcessedEventArgs> RunAsync(
        PrinterConfiguration config, Order order, CancellationToken ct)
    {
        var printService = new OrderPrintService(
            new StubPrinterService(config),
            new NoopRequestLogService(),
            NullLogger<OrderPrintService>.Instance,
            _paths,
            new StubDeviceIdentityService());

        var pipeline = new OrderPipeline(
            _feed,
            printService,
            _history,
            _outbox,
            new StubTelemetryScheduler(),
            new StubPrinterService(config),
            new StubDeviceIdentityService(),
            _requestLog,
            NullLogger<OrderPipeline>.Instance);

        var processed = new TaskCompletionSource<OrderProcessedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.OrderProcessed += (_, e) => processed.TrySetResult(e);

        await pipeline.StartAsync(ct);
        _feed.Emit(order);

        var finished = await Task.WhenAny(processed.Task, Task.Delay(TestTimeout, ct));
        Assert.True(finished == processed.Task, "the pipeline never raised OrderProcessed");
        return await processed.Task;
    }

    private int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>An in-process loopback "printer": one port, and the ticket that arrived on it.</summary>
    private sealed class Sink : IDisposable
    {
        private readonly TcpListener _listener;

        public Sink()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            PrinterName = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}"; // IP literal → NetworkTcpTransport
        }

        public string PrinterName { get; }

        /// <summary>A connection is waiting to be accepted, i.e. something was printed here.</summary>
        public bool ReceivedAnything => _listener.Pending();

        /// <summary>
        /// The ticket as text. Decoded as Latin1: the assertions are ASCII, which PC857 leaves
        /// unchanged.
        /// </summary>
        public async Task<string> ReadTicketAsync(CancellationToken ct)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return Encoding.Latin1.GetString(ms.ToArray());
        }

        public void Dispose() => _listener.Stop();
    }

    private sealed class StubFeed : IEventStreamingService
    {
        private readonly List<string> _confirmed = new();
        private readonly List<string> _released = new();

        public IReadOnlyList<string> Confirmed => _confirmed;
        public IReadOnlyList<string> Released => _released;

        public bool IsListening { get; private set; }
        public DateTime? LastSuccessfulPollAt => null;
        public event EventHandler<OrderEvent>? OrderReceived;
        public event EventHandler<PrinterFeedUpdate>? UpdateReceived { add { } remove { } }
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

        public void ConfirmOrderHandled(string orderNumber) => _confirmed.Add(orderNumber);

        public void ReleaseOrderForRetry(string orderNumber, DateTime? createdAt = null, DateTime? updatedAt = null) =>
            _released.Add(orderNumber);

        public void Emit(Order order) =>
            OrderReceived?.Invoke(this, new OrderEvent
            {
                EventType = "order-created",
                Order = order,
                Timestamp = DateTime.UtcNow,
            });
    }

    private sealed class RecordingHistory : IOrderHistoryService
    {
        public List<(string OrderId, bool KitchenPrinted, bool CashierPrinted)> Updates { get; } = new();

        public ReadOnlyObservableCollection<OrderHistoryItem> Orders { get; } =
            new(new ObservableCollection<OrderHistoryItem>());

        public event EventHandler<OrderHistoryItem>? OrderAdded { add { } remove { } }

        public void AddOrder(OrderEvent orderEvent) { }

        public void UpdatePrintStatus(string orderId, bool kitchenPrinted, bool cashierPrinted) =>
            Updates.Add((orderId, kitchenPrinted, cashierPrinted));

        public void ClearHistory() { }

        public OrderHistoryItem? GetOrder(string orderId) => null;
    }

    private sealed class RecordingOutbox : IPrintAckOutbox
    {
        public List<PrintAck> Enqueued { get; } = new();
        public Exception? EnqueueFailure { get; set; }
        public Action? DuringEnqueue { get; set; }

        public Task EnqueueAsync(IEnumerable<PrintAck> acks, CancellationToken cancellationToken = default)
        {
            DuringEnqueue?.Invoke();
            if (EnqueueFailure is not null)
                throw EnqueueFailure;

            Enqueued.AddRange(acks);
            return Task.CompletedTask;
        }

        public Task FlushAsync(Func<IReadOnlyList<PrintAck>, Task<bool>> sender, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRequestLog : NoopRequestLogService
    {
        public List<string> Errors { get; } = new();

        public override void LogError(string operation, string message, string? details = null) =>
            Errors.Add($"{operation}: {message} {details}");
    }

    private sealed class StubTelemetryScheduler : ITelemetryScheduler
    {
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class StubDeviceIdentityService : IDeviceIdentityService
    {
        public string DeviceId => "test-device";
        public string Platform => "Test";
        public string AppVersion => "test";
        public void ApplySentryTags(string tenantSlug) { }
    }

    private sealed class StubPrinterService : IPrinterService
    {
        private readonly PrinterConfiguration _config;
        public StubPrinterService(PrinterConfiguration config) => _config = config;
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(_config);
        public string ConfigFilePath => "(test)";
        public Task<List<string>> GetAvailablePrintersAsync() => throw new NotSupportedException();
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) => throw new NotSupportedException();
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) => throw new NotSupportedException();
        public Task SaveConfigurationAsync(PrinterConfiguration config) => throw new NotSupportedException();
    }
}
