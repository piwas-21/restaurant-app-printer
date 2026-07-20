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
