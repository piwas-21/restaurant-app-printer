using System.Net;
using System.Net.Sockets;
using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class IosPilotSafetyTests
{
    [Fact]
    public async Task Apple_updates_never_offer_or_run_a_downloaded_installer()
    {
        var service = new AppleUpdateService();
        var update = await service.CheckForUpdateAsync();
        Assert.False(update.UpdateAvailable);
        Assert.Empty(update.DownloadUrl);
        Assert.Equal(UpdateInstallOutcome.Failed,
            await service.DownloadAndInstallUpdateAsync(new UpdateInfo
            {
                UpdateAvailable = true,
                DownloadUrl = "untrusted-installer.exe"
            }));
        Assert.Null(UpdateAssetSelector.Select(new[]
        {
            new GitHubAsset { Name = "PrinterApp-Setup-x64.exe" },
            new GitHubAsset { Name = "PrinterApp-Android.apk" },
            new GitHubAsset { Name = "PrinterApp.ipa" }
        }, UpdatePlatform.Ios, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Named_spooler_targets_are_rejected_before_configuration_is_saved(int target)
    {
        var config = new PrinterConfiguration();
        const string name = "Installed Windows queue";
        switch (target)
        {
            case 0: config.KitchenPrinterName = name; break;
            case 1: config.DefaultKitchenPrinterName = name; break;
            case 2: config.FrontKitchenPrinterName = name; break;
            case 3: config.BackKitchenPrinterName = name; break;
            case 4: config.CashierPrinterName = name; break;
        }
        var store = new ConfigurationStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new NetworkPrinterService(store).SaveConfigurationAsync(config));
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Valid_network_targets_persist_without_creating_a_default_Windows_queue()
    {
        var config = new PrinterConfiguration
        {
            KitchenPrinterName = "192.0.2.1",
            DefaultKitchenPrinterName = "192.0.2.2:9100",
            FrontKitchenPrinterName = "192.0.2.3",
            BackKitchenPrinterName = "192.0.2.4",
            CashierPrinterName = "192.0.2.5"
        };
        var store = new ConfigurationStore();
        var service = new NetworkPrinterService(store);
        await service.SaveConfigurationAsync(config);
        Assert.Same(config, store.Saved);
        Assert.Empty(await service.GetAvailablePrintersAsync());
        Assert.False(await service.PrintTestReceiptAsync("Installed Windows queue", config));
    }

    [Fact]
    public async Task Network_service_sends_a_real_ESC_POS_test_receipt_to_a_TCP_sink()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        var target = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var send = new NetworkPrinterService(new ConfigurationStore())
            .PrintTestReceiptAsync(target, new PrinterConfiguration { RestaurantName = "Pilot" });
        using var client = await accept;
        await using var bytes = new MemoryStream();
        await client.GetStream().CopyToAsync(bytes, timeout.Token);
        Assert.True(await send);
        var receipt = bytes.ToArray();
        Assert.Equal(new byte[] { 0x1b, 0x40 }, receipt.Take(2).ToArray());
        Assert.Contains("RUMI Printer Test\nPilot\n", Encoding.ASCII.GetString(receipt));
        Assert.Equal(new byte[] { 0x1d, 0x56, 0x00 }, receipt.TakeLast(3).ToArray());
    }

    private sealed class ConfigurationStore : IPrinterConfigurationStore
    {
        public string ConfigFilePath => string.Empty;
        public PrinterConfiguration? Saved { get; private set; }
        public Task<PrinterConfiguration> LoadAsync() => Task.FromResult(Saved ?? new PrinterConfiguration());
        public Task SaveAsync(PrinterConfiguration config)
        {
            Saved = config;
            return Task.CompletedTask;
        }
    }
}
