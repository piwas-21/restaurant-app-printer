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
/// Print-to-sink integration for <see cref="OrderPrintService"/> (A3): the REAL receipt composer drives a
/// real network transport into an in-process <see cref="TcpListener"/> on loopback — no printer hardware.
/// Proves the whole compose→route→send path (not just formatting): an IP-literal printer name resolves to
/// <see cref="NetworkTcpTransport"/> and the ESC/POS bytes for a real order land at the socket, framed by
/// the init + cut commands. Hermetic (no backend) → runs on every PR. See docs/E2E-STRATEGY.md.
/// </summary>
public class OrderPrintToSinkTests
{
    [Fact]
    public async Task PrintOrderAsync_Cashier_SendsFramedEscPosReceipt_ToNetworkSink()
    {
        // In-process loopback "printer": accept one connection and read every byte the app sends.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var receivedTask = AcceptAndReadAllAsync(listener, cts.Token);

            using var paths = new TempPathProvider();
            var config = new PrinterConfiguration
            {
                CashierPrinterName = $"127.0.0.1:{port}", // IP literal → NetworkTcpTransport
                CashierAutoPrint = true,
                CashierPrintCopies = 1,
            };
            var service = new OrderPrintService(
                new StubPrinterService(config),
                new CapturingRequestLogService(),
                NullLogger<OrderPrintService>.Instance,
                paths);

            var order = new Order
            {
                OrderNumber = "E2E-4242",
                Type = "DineIn",
                TableId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                TableLabel = "T-QA",
                Status = "Confirmed",
                SubTotal = 16.50m,
                Total = 16.50m,
                OrderDate = DateTime.Now,
                Items =
                {
                    new OrderItem { ProductName = "Adana Kebab", Quantity = 2, UnitPrice = 8.25m, ItemTotal = 16.50m },
                },
            };

            // isManualPrint: true bypasses the auto-print/time-restriction short-circuits so the receipt
            // is actually composed and sent (the path under test).
            var ok = await service.PrintOrderAsync(order, PrinterType.Cashier, isManualPrint: true, cts.Token);
            Assert.True(ok, "PrintOrderAsync reported failure");

            var bytes = await receivedTask;

            Assert.NotEmpty(bytes);
            Assert.True(SubsequenceIndex(bytes, new byte[] { 0x1B, 0x40 }) >= 0, "no ESC @ init command in the stream");
            Assert.True(SubsequenceIndex(bytes, new byte[] { 0x1D, 0x56 }) >= 0, "no GS V cut command in the stream");
            // The order's human content made it into the receipt (ASCII survives PC857 unchanged).
            Assert.True(SubsequenceIndex(bytes, Encoding.ASCII.GetBytes("E2E-4242")) >= 0, "order number missing");
            Assert.True(SubsequenceIndex(bytes, Encoding.ASCII.GetBytes("Adana Kebab")) >= 0, "item name missing");
            Assert.True(SubsequenceIndex(bytes, Encoding.ASCII.GetBytes("Table T-QA")) >= 0, "stable table label missing");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// <see cref="OrderPrintService"/> hands the print log the order's real number, verbatim. The
    /// log used to take an <c>int orderId</c> derived as
    /// <c>int.TryParse(OrderNumber.Split('/').Last())</c>; a real order number is yyyyMMdd + a
    /// 4-digit sequence (backend OrderNumberGenerator), i.e. 12 digits — past
    /// <see cref="int.MaxValue"/>, so the parse failed and every print line in the log said
    /// "Order #0". The order number below is that exact shape.
    /// <para>
    /// Scope: this covers the print half only. The feed half (EventStreamingService) and the
    /// rendered log text both live behind MAUI's MainThread and are not source-linkable here — the
    /// parameter being a string is what stops the lossy derivation coming back there.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrintOrderAsync_LogsTheRealOrderNumber_NotAParsedInt()
    {
        const string orderNumber = "202607290001"; // 12 digits: > int.MaxValue, and no '/' to split on
        using var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var paths = new TempPathProvider();

        var log = new CapturingRequestLogService();
        var service = new OrderPrintService(
            new StubPrinterService(new PrinterConfiguration
            {
                CashierPrinterName = sink.PrinterName,
                CashierAutoPrint = true,
                CashierPrintCopies = 1,
            }),
            log,
            NullLogger<OrderPrintService>.Instance,
            paths);

        var order = new Order
        {
            OrderNumber = orderNumber,
            Type = "DineIn",
            TableNumber = 4,
            Status = "Confirmed",
            Total = 12.00m,
            OrderDate = DateTime.Now,
            Items = { new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 12.00m, ItemTotal = 12.00m } },
        };

        Assert.True(await service.PrintOrderAsync(order, PrinterType.Cashier, isManualPrint: true, cts.Token),
            "PrintOrderAsync reported failure");
        await sink.ReadTicketAsync(cts.Token); // drain the socket so the sink closes cleanly

        Assert.NotEmpty(log.PrintLogOrderNumbers);
        Assert.All(log.PrintLogOrderNumbers, logged => Assert.Equal(orderNumber, logged));
    }

    /// <summary>
    /// A bundle every component of which one kitchen makes: one ticket, components nested under
    /// their combo, each printed exactly once — no top-level duplicate of a nested component, and
    /// nothing at all for the other kitchen.
    /// </summary>
    [Fact]
    public async Task PrintOrderToAllPrinters_SingleKitchenBundle_PrintsOneTicket_WithComponentsNestedOnce()
    {
        using var cashier = new Sink();
        using var front = new Sink();
        using var back = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var order = BundleOrder(
            Item("Menu Deal", "FrontKitchen", children:
            [
                Item("Kofte", "FrontKitchen"),
                Item("Ayran", kitchenType: null), // no kitchen of its own: rides with its parent
            ]));

        var result = await PrintToSinksAsync(order, cashier, front, back, cts.Token);

        Assert.True(result.FrontKitchen, "front kitchen print reported failure");
        var ticket = await front.ReadTicketAsync(cts.Token);
        Assert.Equal(1, Occurrences(ticket, "Menu Deal"));
        Assert.Equal(1, Occurrences(ticket, "Kofte"));
        Assert.Equal(1, Occurrences(ticket, "Ayran"));
        // Everything on this ticket is this kitchen's work, so nothing is parenthesised as
        // context — including the drink, which has no KitchenType of its own.
        Assert.DoesNotContain("(1x", ticket);

        // The kitchen with nothing to make is never contacted.
        Assert.False(back.ReceivedAnything, "back kitchen was sent a ticket it has nothing to make");
    }

    /// <summary>
    /// The reported failure: a FrontKitchen "Menu Deal" containing BackKitchen fries. Before the
    /// fix the top-level-only scan saw no BackKitchen item, so the back kitchen got NO ticket and
    /// the fries printed on the front kitchen's ticket as a nested "+ 2x Fries" line.
    /// </summary>
    [Fact]
    public async Task PrintOrderToAllPrinters_MixedKitchenBundle_PrintsEachComponentOnItsOwnKitchenTicket()
    {
        using var cashier = new Sink();
        using var front = new Sink();
        using var back = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var order = BundleOrder(
            Item("Menu Deal", "FrontKitchen", children:
            [
                Item("Kofte", "FrontKitchen"),
                Item("Fries", "BackKitchen", quantity: 2),
            ]));

        var result = await PrintToSinksAsync(order, cashier, front, back, cts.Token);

        Assert.True(result.FrontKitchen, "front kitchen print reported failure");
        Assert.True(result.BackKitchen, "back kitchen print reported failure");

        var frontTicket = await front.ReadTicketAsync(cts.Token);
        Assert.Equal(1, Occurrences(frontTicket, "Menu Deal"));
        Assert.Equal(1, Occurrences(frontTicket, "Kofte"));
        Assert.Equal(0, Occurrences(frontTicket, "Fries")); // the regression: fries rode along here

        var backTicket = await back.ReadTicketAsync(cts.Token);
        Assert.Equal(1, Occurrences(backTicket, "Fries")); // the regression: this ticket never printed
        Assert.Contains("2x Fries", backTicket);
        Assert.Equal(0, Occurrences(backTicket, "Kofte"));
        // The combo line rides along as context for the component, parenthesised so it does not
        // read as a dish the back kitchen has to make.
        Assert.Contains("(1x Menu Deal)", backTicket);
    }

    /// <summary>
    /// The partner complaint, end to end: a cashier receipt must carry the ingredient rows the
    /// backend froze at checkout (a quantity-one selection is an explicit choice, not a default to
    /// hide) and the bundle components hanging under an item — through the real compose→send path.
    /// </summary>
    [Fact]
    public async Task PrintOrderAsync_Cashier_PrintsIngredientCustomizations_AndComponents()
    {
        using var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var order = new Order
        {
            OrderNumber = "202609040001",
            Type = "TakeAway",
            Status = "Confirmed",
            Total = 21.40m,
            OrderDate = DateTime.Now,
            Items =
            {
                new OrderItem
                {
                    ProductName = "Adana Kebab",
                    Quantity = 1,
                    ItemTotal = 21.40m,
                    IngredientCustomizations =
                    [
                        new IngredientCustomization { IngredientName = "Onion", Quantity = 0, IsRemoved = true },
                        new IngredientCustomization { IngredientName = "Hot Sauce", Quantity = 1, IsRemoved = false },
                    ],
                    SideItems = [new OrderItem { ProductName = "Ayran", Quantity = 1 }],
                },
            },
        };

        var (ok, ticket) = await PrintToSinkAsync(order, sink, cts.Token);

        Assert.True(ok, "PrintOrderAsync reported failure");
        Assert.Contains("- NO Onion", ticket);
        Assert.Contains("+ Hot Sauce", ticket); // selected at quantity one — used to be filtered off
        Assert.Contains("+ 1x Ayran", ticket);  // side item — used to be cashier-invisible
        Assert.Contains("Adana Kebab", ticket);
    }

    /// <summary>A fixed venue language choice must localize the receipt labels on the wire.</summary>
    [Fact]
    public async Task PrintOrderAsync_Cashier_LocalizesLabels_ToTheConfiguredLanguage()
    {
        using var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var order = MinimalOrder();

        var (ok, ticket) = await PrintToSinkAsync(order, sink, cts.Token,
            config => config.PrintLanguage = "de");

        Assert.True(ok, "PrintOrderAsync reported failure");
        Assert.Contains("Zwischensumme", ticket); // localized Subtotal
        Assert.Contains("Im Lokal", ticket);      // localized DineIn
        Assert.DoesNotContain("Subtotal", ticket);
    }

    /// <summary>On-site card intents print a clear payment label rather than the backend enum name.</summary>
    [Fact]
    public async Task PrintOrderAsync_Cashier_LocalizesCreditCardAsCardAtRestaurant()
    {
        using var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var order = MinimalOrder();
        order.Payments =
        [
            new Payment
            {
                PaymentMethod = "CreditCard",
                Amount = order.Total,
                Status = "Pending",
            },
        ];

        var (ok, ticket) = await PrintToSinkAsync(order, sink, cts.Token);

        Assert.True(ok, "PrintOrderAsync reported failure");
        Assert.Contains("CARD AT RESTAURANT", ticket);
        Assert.DoesNotContain("CreditCard", ticket);
    }

    /// <summary>
    /// The "order language" option: no fixed choice, the guest's PreferredLanguage picks the labels,
    /// with English where the order carries none.
    /// </summary>
    [Fact]
    public async Task PrintOrderAsync_Cashier_AutoLanguage_FollowsTheOrdersPreferredLanguage()
    {
        using var frenchSink = new Sink();
        using var englishSink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var french = MinimalOrder();
        french.PreferredLanguage = "fr";
        var (okFrench, frenchTicket) = await PrintToSinkAsync(french, frenchSink, cts.Token,
            config => config.PrintLanguage = PrintLanguagePolicy.Auto);
        Assert.True(okFrench, "french print reported failure");
        Assert.Contains("Sous-total", frenchTicket);

        var english = MinimalOrder();
        var (okEnglish, englishTicket) = await PrintToSinkAsync(english, englishSink, cts.Token,
            config => config.PrintLanguage = PrintLanguagePolicy.Auto);
        Assert.True(okEnglish, "english print reported failure");
        Assert.Contains("Subtotal", englishTicket);
    }

    private static Order MinimalOrder() => new()
    {
        OrderNumber = "202609040002",
        Type = "DineIn",
        TableNumber = 5,
        Status = "Confirmed",
        SubTotal = 12.50m,
        Total = 12.50m,
        OrderDate = DateTime.Now,
        Items = { new OrderItem { ProductName = "Pide", Quantity = 1, ItemTotal = 12.50m } },
    };

    private static async Task<(bool Ok, string Ticket)> PrintToSinkAsync(
        Order order, Sink sink, CancellationToken ct,
        Action<PrinterConfiguration>? configure = null)
    {
        using var paths = new TempPathProvider();
        var config = new PrinterConfiguration
        {
            CashierPrinterName = sink.PrinterName,
            CashierAutoPrint = true,
            CashierPrintCopies = 1,
        };
        configure?.Invoke(config);

        var service = new OrderPrintService(
            new StubPrinterService(config),
            new CapturingRequestLogService(),
            NullLogger<OrderPrintService>.Instance,
            paths);

        var ok = await service.PrintOrderAsync(order, PrinterType.Cashier, isManualPrint: true, ct);
        var ticket = ok ? await sink.ReadTicketAsync(ct) : string.Empty;
        return (ok, ticket);
    }

    private static Order BundleOrder(params OrderItem[] items) => new()
    {
        OrderNumber = "BUNDLE-1",
        Type = "DineIn",
        TableNumber = 3,
        Status = "Confirmed",
        OrderDate = DateTime.Now,
        Items = items.ToList(),
    };

    private static OrderItem Item(
        string productName,
        string? kitchenType,
        int quantity = 1,
        List<OrderItem>? children = null) =>
        new()
        {
            Id = productName,
            ProductName = productName,
            KitchenType = kitchenType,
            Quantity = quantity,
            SideItems = children,
        };

    private static async Task<(bool Cashier, KitchenPrintOutcome FrontKitchen, KitchenPrintOutcome BackKitchen, KitchenPrintOutcome GeneralDefault)>
        PrintToSinksAsync(Order order, Sink cashier, Sink front, Sink back, CancellationToken ct)
    {
        using var paths = new TempPathProvider();
        var service = new OrderPrintService(
            new StubPrinterService(new PrinterConfiguration
            {
                CashierPrinterName = cashier.PrinterName,
                CashierAutoPrint = true,
                CashierPrintCopies = 1,
                FrontKitchenPrinterName = front.PrinterName,
                FrontKitchenAutoPrint = true,
                BackKitchenPrinterName = back.PrinterName,
                BackKitchenAutoPrint = true,
            }),
            new CapturingRequestLogService(),
            NullLogger<OrderPrintService>.Instance,
            paths);

        return await service.PrintOrderToAllPrintersAsync(order, isManualPrint: false, ct);
    }

    private static int Occurrences(string haystack, string needle)
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

        /// <summary>Configure this as a printer name.</summary>
        public string PrinterName { get; }

        /// <summary>A connection is waiting to be accepted, i.e. something was printed here.</summary>
        public bool ReceivedAnything => _listener.Pending();

        /// <summary>
        /// The ticket as text. The sender connects, writes and closes per print, so the connection
        /// sits in the accept backlog until read — no need to race an accept against the print.
        /// Decoded as Latin1: the assertions are ASCII, which PC857 leaves unchanged.
        /// </summary>
        public async Task<string> ReadTicketAsync(CancellationToken ct)
        {
            var bytes = await AcceptAndReadAllAsync(_listener, ct);
            return Encoding.Latin1.GetString(bytes);
        }

        public void Dispose() => _listener.Stop();
    }

    private static async Task<byte[]> AcceptAndReadAllAsync(TcpListener listener, CancellationToken ct)
    {
        using var server = await listener.AcceptTcpClientAsync(ct);
        await using var stream = server.GetStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Index of the first occurrence of <paramref name="needle"/> in <paramref name="haystack"/>, or -1.</summary>
    private static int SubsequenceIndex(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    // ── test-only edges ─────────────────────────────────────────────────────────────────────────────
    private sealed class TempPathProvider : IAppDataPathProvider, IDisposable
    {
        public string AppDataDirectory { get; }
        public string LegacyAppDataDirectory => AppDataDirectory;

        public TempPathProvider()
        {
            AppDataDirectory = Path.Combine(Path.GetTempPath(), "printerapp-print-sink-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AppDataDirectory);
        }

        public void Dispose()
        {
            try { Directory.Delete(AppDataDirectory, recursive: true); } catch { /* best-effort */ }
        }
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

    /// <summary>
    /// Discards everything except the order number each print log line was given — the one thing
    /// <see cref="PrintOrderAsync_LogsTheRealOrderNumber_NotAParsedInt"/> asserts on.
    /// </summary>
    private sealed class CapturingRequestLogService : IRequestLogService
    {
        private readonly object _gate = new();
        private readonly List<string> _printLogOrderNumbers = new();

        /// <summary>Order numbers handed to LogPrintRequest / LogPrintResponse, in call order.</summary>
        public IReadOnlyList<string> PrintLogOrderNumbers
        {
            get { lock (_gate) { return _printLogOrderNumbers.ToArray(); } }
        }

        public ReadOnlyObservableCollection<LogEntry> Logs { get; } = new(new ObservableCollection<LogEntry>());
#pragma warning disable CS0067
        public event EventHandler<LogEntry>? LogAdded;
#pragma warning restore CS0067
        public void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null) { }
        public void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null) { }
        public void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null) { }
        public void LogOrderReceived(
            string orderNumber,
            Guid? tableId,
            string? tableLabel,
            int? tableNumber,
            decimal total,
            string? orderJson = null,
            string? source = null)
        {
        }
        public void LogPrintRequest(string printerType, string orderNumber, string printerName, string? printContent = null)
        {
            lock (_gate) { _printLogOrderNumbers.Add(orderNumber); }
        }
        public void LogPrintResponse(string printerType, string orderNumber, bool success, string? error = null, string? details = null)
        {
            lock (_gate) { _printLogOrderNumbers.Add(orderNumber); }
        }
        public void LogError(string operation, string message, string? details = null) { }
        public void LogWarning(string operation, string message, string? details = null, string? source = null) { }
        public void ClearLogs() { }
    }
}
