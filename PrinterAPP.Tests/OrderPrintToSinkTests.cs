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
                new NoopRequestLogService(),
                NullLogger<OrderPrintService>.Instance,
                paths);

            var order = new Order
            {
                OrderNumber = "E2E-4242",
                Type = "DineIn",
                TableNumber = 7,
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
        }
        finally
        {
            listener.Stop();
        }
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

    private static async Task<(bool Cashier, bool FrontKitchen, bool BackKitchen)> PrintToSinksAsync(
        Order order, Sink cashier, Sink front, Sink back, CancellationToken ct)
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
            new NoopRequestLogService(),
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

    private sealed class NoopRequestLogService : IRequestLogService
    {
        public ReadOnlyObservableCollection<LogEntry> Logs { get; } = new(new ObservableCollection<LogEntry>());
#pragma warning disable CS0067
        public event EventHandler<LogEntry>? LogAdded;
#pragma warning restore CS0067
        public void LogSSEConnection(string endpoint, string status, string? url = null, Dictionary<string, string>? headers = null) { }
        public void LogSSEResponse(string endpoint, int statusCode, Dictionary<string, string>? responseHeaders = null) { }
        public void LogSSEEvent(string eventType, string data, string? rawData = null, string? source = null) { }
        public void LogOrderReceived(int orderId, int? tableNumber, decimal total, string? orderJson = null, string? source = null) { }
        public void LogPrintRequest(string printerType, int orderId, string printerName, string? printContent = null) { }
        public void LogPrintResponse(string printerType, int orderId, bool success, string? error = null, string? details = null) { }
        public void LogError(string operation, string message, string? details = null) { }
        public void LogWarning(string operation, string message, string? details = null, string? source = null) { }
        public void ClearLogs() { }
    }
}
