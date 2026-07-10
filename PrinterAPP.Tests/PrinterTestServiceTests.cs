using System.Net;
using System.Net.Sockets;
using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Routing + message-format tests for <see cref="PrinterTestService.TestPrinterAsync"/>: network
/// targets go over <see cref="NetworkTcpTransport"/> (golden bytes asserted against an in-process
/// listener), spooler targets delegate to <see cref="IPrinterService.PrintTestReceiptAsync"/>
/// unchanged (that is what guarantees byte-identical spooler test receipts), and result lines
/// match the pre-seam MainPage strings exactly.
/// </summary>
public class PrinterTestServiceTests
{
    /// <summary>Recording <see cref="IPrinterService"/> double for the spooler test path.</summary>
    private sealed class FakePrinterService : IPrinterService
    {
        public bool TestReceiptResult { get; set; } = true;

        public List<(string PrinterName, PrinterConfiguration Config)> TestReceiptCalls { get; } = new();

        public string ConfigFilePath => "/dev/null/config.json";

        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config)
        {
            TestReceiptCalls.Add((printerName, config));
            return Task.FromResult(TestReceiptResult);
        }

        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());

        public Task<bool> TestApiConnectionAsync(string apiUrl) => Task.FromResult(false);

        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(new PrinterConfiguration());

        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }

    private static PrinterTestService CreateService(FakePrinterService? printerService = null)
        => new(printerService ?? new FakePrinterService());

    private static PrinterTestTarget Target(string? networkAddress, string? spoolerName)
    {
        var target = PrinterTestTarget.Resolve(networkAddress, spoolerName);
        Assert.NotNull(target);
        return target;
    }

    [Fact]
    public async Task Spooler_target_delegates_to_legacy_test_receipt_path_verbatim()
    {
        var fake = new FakePrinterService { TestReceiptResult = true };
        var service = CreateService(fake);
        var config = new PrinterConfiguration();

        var result = await service.TestPrinterAsync(
            Target(null, "EPSON TM-T20II (Default)"), config, "KITCHEN", "Kitchen");

        var call = Assert.Single(fake.TestReceiptCalls);
        Assert.Equal("EPSON TM-T20II (Default)", call.PrinterName); // name passed unstripped, as before
        Assert.Same(config, call.Config);
        Assert.Equal("Kitchen printer: ✓ Success", result);
    }

    [Fact]
    public async Task Spooler_target_failure_message_matches_legacy_format()
    {
        var fake = new FakePrinterService { TestReceiptResult = false };
        var service = CreateService(fake);

        var result = await service.TestPrinterAsync(
            Target(null, "EPSON TM-T20II"), new PrinterConfiguration(), "CASHIER", "Cashier");

        Assert.Equal("Cashier printer: ✗ Failed", result);
    }

    [Fact]
    public async Task Invalid_network_target_reports_invalid_ip_and_never_touches_the_spooler()
    {
        var fake = new FakePrinterService();
        var service = CreateService(fake);

        var result = await service.TestPrinterAsync(
            Target(" not-an-ip ", "EPSON TM-T20II"), new PrinterConfiguration(), "KITCHEN", "Kitchen");

        Assert.Equal("Kitchen printer: ✗ Invalid IP 'not-an-ip'", result);
        Assert.Empty(fake.TestReceiptCalls);
    }

    [Fact]
    public async Task Network_target_sends_golden_test_receipt_bytes_over_tcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receivedTask = Task.Run(async () =>
            {
                using var server = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = server.GetStream();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cts.Token);
                return ms.ToArray();
            }, cts.Token);

            var fake = new FakePrinterService();
            var service = CreateService(fake);
            var result = await service.TestPrinterAsync(
                Target($"127.0.0.1:{port}", null), new PrinterConfiguration(), "KITCHEN", "Kitchen");

            Assert.Equal($"Kitchen printer (network 127.0.0.1:{port}): ✓ Success", result);
            Assert.Empty(fake.TestReceiptCalls);

            // Golden bytes around the timestamp line (which is the only non-deterministic part):
            // PC857-encoded ESC/POS with init, Turkish codepage, centered header + label, and the
            // left-align / feed / full-cut trailer.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var pc857 = Encoding.GetEncoding(857);
            var expectedPrefix = pc857.GetBytes(
                EscPosCommands.Initialize
                + EscPosCommands.CodepageTurkish
                + EscPosCommands.AlignCenter
                + "RUMI Printer Test\n"
                + "KITCHEN\n"
                + "Turkce: çÇğĞşŞüÜıİöÖ\n");
            var expectedSuffix = pc857.GetBytes(
                "\n" + EscPosCommands.AlignLeft + EscPosCommands.Feed3Lines + EscPosCommands.FullCut);

            var received = await receivedTask;
            Assert.Equal(expectedPrefix, received.Take(expectedPrefix.Length).ToArray());
            Assert.Equal(expectedSuffix, received.TakeLast(expectedSuffix.Length).ToArray());
            // Between prefix and suffix sits the "yyyy-MM-dd HH:mm:ss" timestamp (19 chars, PC857
            // single-byte).
            Assert.Equal(expectedPrefix.Length + 19 + expectedSuffix.Length, received.Length);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Unreachable_network_target_reports_failure_without_throwing()
    {
        // Bind+release an ephemeral loopback port so nothing is listening on it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var service = CreateService();
        var result = await service.TestPrinterAsync(
            Target($"127.0.0.1:{port}", null), new PrinterConfiguration(), "CASHIER", "Cashier");

        Assert.StartsWith($"Cashier printer (network 127.0.0.1:{port}): ✗ ", result);
    }
}
