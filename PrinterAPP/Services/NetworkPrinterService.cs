using System.Net;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Network-only printer settings for the iOS pilot; no Win32 enumeration or spooler fallback.</summary>
public sealed class NetworkPrinterService : IPrinterService
{
    private readonly IPrinterConfigurationStore _configStore;

    public NetworkPrinterService(IPrinterConfigurationStore configStore) => _configStore = configStore;

    public string ConfigFilePath => _configStore.ConfigFilePath;

    public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());

    public async Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config)
    {
        if (!PrinterTargetEntry.IsNetworkFieldTarget(printerName))
            return false;

        try
        {
            await PrinterTransportResolver.Resolve(printerName).SendAsync(
                PrinterTestService.BuildTestReceipt(config.RestaurantName), CancellationToken.None);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey)
    {
        var config = await _configStore.LoadAsync();
        return await PrinterFeedProbe.TestAsync(apiUrl, apiKey, TimeSpan.FromSeconds(config.FeedProbeTimeoutSeconds));
    }

    public Task<PrinterConfiguration> LoadConfigurationAsync() => _configStore.LoadAsync();

    public Task SaveConfigurationAsync(PrinterConfiguration config)
    {
        var targets = new[]
        {
            config.KitchenPrinterName, config.DefaultKitchenPrinterName,
            config.FrontKitchenPrinterName, config.BackKitchenPrinterName, config.CashierPrinterName
        };
        if (targets.Any(static target =>
            !string.IsNullOrWhiteSpace(target) && !PrinterTargetEntry.IsNetworkFieldTarget(target)))
            throw new ArgumentException("iOS printers must use a network IP address or a file capture target.");

        return _configStore.SaveAsync(config);
    }
}
