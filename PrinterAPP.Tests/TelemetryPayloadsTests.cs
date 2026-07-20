using System.Text.Json;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class TelemetryPayloadsTests
{
    [Fact]
    public void Heartbeat_MapsNonSecretConfigAndState()
    {
        var config = new PrinterConfiguration
        {
            DeviceLabel = "Cashier tablet",
            TenantSlug = "rumi",
            ApiBaseUrl = "https://api.example.com",
            CashierPrinterName = "192.168.1.51",
        };
        var pollAt = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

        var hb = TelemetryPayloads.Heartbeat(config, "Android", "1.0.20", feedRunning: true, pollAt);

        Assert.Equal("Cashier tablet", hb.Label);
        Assert.Equal("rumi", hb.TenantSlug);
        Assert.Equal("Android", hb.Platform);
        Assert.Equal("1.0.20", hb.AppVersion);
        Assert.True(hb.FeedRunning!.Value);
        Assert.Equal(pollAt, hb.LastSuccessfulPollAt);
        Assert.Equal("https://api.example.com", hb.ApiBaseUrl);
        Assert.Equal("192.168.1.51", hb.CashierPrinter);
    }

    [Fact]
    public void Heartbeat_NeverIncludesApiKey_EvenSerialized()
    {
        var config = new PrinterConfiguration
        {
            ApiKey = "TOP-SECRET-KEY",  // pragma: allowlist secret — test sentinel, not a real key
            ApiBaseUrl = "https://api.example.com",
        };

        var hb = TelemetryPayloads.Heartbeat(config, "Android", "1.0.20", true, null);
        var json = JsonSerializer.Serialize(hb);

        Assert.DoesNotContain("TOP-SECRET-KEY", json);
    }

    [Fact]
    public void Heartbeat_ComposesConfiguredKitchenPrinters()
    {
        var config = new PrinterConfiguration
        {
            FrontKitchenPrinterName = "192.168.1.50",
            BackKitchenPrinterName = "192.168.1.60",
            KitchenPrinterName = "",
        };

        var hb = TelemetryPayloads.Heartbeat(config, "Android", "1.0.20", true, null);

        Assert.Equal("192.168.1.50, 192.168.1.60", hb.KitchenPrinter);
    }

    [Fact]
    public void Heartbeat_BlankConfigFields_BecomeNull()
    {
        var config = new PrinterConfiguration
        {
            DeviceLabel = "",
            TenantSlug = "",
            FrontKitchenPrinterName = "",
            BackKitchenPrinterName = "",
            KitchenPrinterName = "",
            CashierPrinterName = "",
        };

        var hb = TelemetryPayloads.Heartbeat(config, "Android", "1.0.20", false, null);

        Assert.Null(hb.Label);
        Assert.Null(hb.TenantSlug);
        Assert.Null(hb.KitchenPrinter);
        Assert.Null(hb.CashierPrinter);
    }
}
