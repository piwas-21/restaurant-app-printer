using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class KitchenRoutingSettingsMapperTests
{
    [Fact]
    public void Load_uses_explicit_default_before_legacy_fallback_and_maps_each_station()
    {
        var projection = KitchenRoutingSettingsMapper.Load(new PrinterConfiguration
        {
            DefaultKitchenPrinterName = "file:/tmp/general",
            KitchenPrinterName = "legacy-general",
            FrontKitchenPrinterName = " 192.168.1.10:9100 ",
            BackKitchenPrinterName = "Back Queue (Default)",
            KitchenRoutingMode = KitchenRoutingMode.SingleKitchen,
        });

        Assert.Equal(KitchenRoutingMode.SingleKitchen, projection.Mode);
        Assert.Equal("file:/tmp/general", projection.GeneralTarget.NetworkAddress);
        Assert.Null(projection.GeneralTarget.SpoolerPrinterName);
        Assert.Equal("192.168.1.10:9100", projection.FrontTarget.NetworkAddress);
        Assert.Equal("Back Queue", projection.BackTarget.SpoolerPrinterName);
        Assert.False(projection.UsesLegacyGeneralFallback);
    }

    [Fact]
    public void Load_shows_legacy_general_target_without_promoting_it_to_explicit_default()
    {
        var config = new PrinterConfiguration { KitchenPrinterName = "legacy-general" };

        var projection = KitchenRoutingSettingsMapper.Load(config);
        var edited = KitchenRoutingSettingsMapper.ForSave(
            projection,
            projection.Mode,
            projection.GeneralTarget,
            projection.FrontTarget,
            projection.BackTarget);

        KitchenRoutingSettingsMapper.Apply(config, edited);

        Assert.Equal("legacy-general", projection.GeneralTarget.SpoolerPrinterName);
        Assert.True(edited.UsesLegacyGeneralFallback);
        Assert.Equal(string.Empty, config.DefaultKitchenPrinterName);
        Assert.Equal("legacy-general", config.KitchenPrinterName);
    }

    [Fact]
    public void Apply_new_general_target_sets_explicit_default_and_preserves_legacy_value()
    {
        var config = new PrinterConfiguration { KitchenPrinterName = "legacy-general" };
        var loaded = KitchenRoutingSettingsMapper.Load(config);
        var edited = KitchenRoutingSettingsMapper.ForSave(
            loaded,
            KitchenRoutingMode.Stations,
            new KitchenRoutingTargetInput("192.168.1.50:9100", null),
            new KitchenRoutingTargetInput(null, "Front Queue"),
            new KitchenRoutingTargetInput(null, "Back Queue"));

        var validation = KitchenRoutingSettingsMapper.Validate(edited);
        Assert.True(validation.IsValid, validation.ErrorMessage);
        KitchenRoutingSettingsMapper.Apply(config, edited);

        Assert.Equal("192.168.1.50:9100", config.DefaultKitchenPrinterName);
        Assert.Equal("legacy-general", config.KitchenPrinterName);
        Assert.Equal("Front Queue", config.FrontKitchenPrinterName);
        Assert.Equal("Back Queue", config.BackKitchenPrinterName);
        Assert.Equal(KitchenRoutingMode.Stations, config.KitchenRoutingMode);
    }

    [Fact]
    public void WithSavedSpoolerTargets_keeps_disconnected_routing_queues_visible()
    {
        var settings = KitchenRoutingSettingsMapper.Load(new PrinterConfiguration
        {
            DefaultKitchenPrinterName = "General Offline",
            FrontKitchenPrinterName = "Front Queue (Default)",
            BackKitchenPrinterName = "192.168.1.60:9100",
        });

        var choices = KitchenRoutingSettingsMapper.WithSavedSpoolerTargets(
            new[] { "Online Queue", "General Offline (Default)" }, settings);

        Assert.Equal(
            new[] { "Online Queue", "General Offline (Default)", "Front Queue" },
            choices);
    }

    [Fact]
    public void Validate_rejects_invalid_network_entry_but_accepts_spooler_and_file_targets()
    {
        var invalid = new KitchenRoutingSettingsProjection(
            KitchenRoutingMode.Stations,
            new KitchenRoutingTargetInput("not-an-ip", null),
            new KitchenRoutingTargetInput(null, "Front Queue"),
            new KitchenRoutingTargetInput("file:/tmp/captures", null));

        var validation = KitchenRoutingSettingsMapper.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains("General / Default", validation.ErrorMessage);
    }

    [Fact]
    public void Load_invalid_persisted_mode_defaults_to_stations_without_touching_targets()
    {
        var config = new PrinterConfiguration
        {
            KitchenRoutingMode = (KitchenRoutingMode)999,
            DefaultKitchenPrinterName = "general",
        };

        var projection = KitchenRoutingSettingsMapper.Load(config);

        Assert.Equal(KitchenRoutingMode.Stations, projection.Mode);
        Assert.Equal("general", projection.GeneralTarget.SpoolerPrinterName);
        Assert.True(KitchenRoutingSettingsMapper.Validate(projection).IsValid);
    }

    [Fact]
    public void Reset_projection_has_safe_defaults()
    {
        var projection = KitchenRoutingSettingsMapper.Load(new PrinterConfiguration());

        Assert.Equal(KitchenRoutingMode.Stations, projection.Mode);
        Assert.Null(projection.GeneralTarget.ToStoredValue());
        Assert.Null(projection.FrontTarget.ToStoredValue());
        Assert.Null(projection.BackTarget.ToStoredValue());
        Assert.True(KitchenRoutingSettingsMapper.Validate(projection).IsValid);
    }
}
