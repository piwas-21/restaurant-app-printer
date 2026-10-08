using System.Text.Json;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// <see cref="PrinterConfigurationStore"/> runs on the live client machine, so its migration
/// behaviour is pinned here: the legacy %APPDATA%/KitchenPrinter/config.json is only ever COPIED
/// (never rewritten or deleted — it is the rollback path), and the API key leaves the plaintext
/// file only after a verified secret-store round-trip. When the secret store is unavailable
/// (SecureStorage on unpackaged Windows) everything must behave exactly like the pre-migration
/// plaintext status quo.
/// </summary>
public sealed class PrinterConfigurationStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly FakeAppDataPathProvider _paths = new();
    private readonly FakeSecretStore _secrets = new();

    public void Dispose() => _paths.Dispose();

    private PrinterConfigurationStore CreateStore() => new(_paths, _secrets);

    private string NewConfigPath => Path.Combine(_paths.AppDataDirectory, "config.json");

    private string LegacyConfigPath =>
        Path.Combine(_paths.LegacyAppDataDirectory, "KitchenPrinter", "config.json");

    private static void WriteConfig(string path, PrinterConfiguration config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Indented));
    }

    private static PrinterConfiguration ReadConfig(string path) =>
        JsonSerializer.Deserialize<PrinterConfiguration>(File.ReadAllText(path))!;

    // --- Path migration (copy-only) ---

    [Fact]
    public async Task Load_fresh_install_returns_defaults_and_creates_no_files()
    {
        var config = await CreateStore().LoadAsync();

        Assert.Equal(string.Empty, config.ApiBaseUrl);
        Assert.Equal(string.Empty, config.ApiKey);
        Assert.False(config.IsServiceRunning);
        Assert.False(FeedStartupPolicy.ShouldBeListening(config));
        Assert.False(File.Exists(NewConfigPath));
        Assert.Equal(0, _secrets.SetCalls);
    }

    [Fact]
    public async Task Load_missing_config_stays_unconfigured_even_if_a_secret_key_remains()
    {
        await _secrets.TrySetAsync(PrinterConfigurationStore.ApiKeySecretName, "stored-key-43");

        var config = await CreateStore().LoadAsync();

        Assert.Equal(string.Empty, config.ApiBaseUrl);
        Assert.Equal("stored-key-43", config.ApiKey);
        Assert.False(FeedStartupPolicy.ShouldBeListening(config));
        Assert.False(File.Exists(NewConfigPath));
    }

    [Fact]
    public async Task Load_copies_legacy_config_to_new_location_and_leaves_original_untouched()
    {
        WriteConfig(LegacyConfigPath, new PrinterConfiguration { RestaurantName = "Legacy Kebab" });
        var legacyBytesBefore = File.ReadAllText(LegacyConfigPath);

        var config = await CreateStore().LoadAsync();

        Assert.Equal("Legacy Kebab", config.RestaurantName);
        Assert.True(File.Exists(NewConfigPath));
        Assert.Equal("Legacy Kebab", ReadConfig(NewConfigPath).RestaurantName);
        Assert.Equal(legacyBytesBefore, File.ReadAllText(LegacyConfigPath));
    }

    [Fact]
    public async Task Load_prefers_new_location_when_both_exist()
    {
        WriteConfig(NewConfigPath, new PrinterConfiguration { RestaurantName = "New Name" });
        WriteConfig(LegacyConfigPath, new PrinterConfiguration { RestaurantName = "Old Name" });
        var legacyBytesBefore = File.ReadAllText(LegacyConfigPath);
        var newBytesBefore = File.ReadAllText(NewConfigPath);

        var config = await CreateStore().LoadAsync();

        Assert.Equal("New Name", config.RestaurantName);
        Assert.Equal(legacyBytesBefore, File.ReadAllText(LegacyConfigPath));
        Assert.Equal(newBytesBefore, File.ReadAllText(NewConfigPath));
    }

    [Fact]
    public async Task Load_falls_back_to_legacy_when_new_location_is_unusable()
    {
        // A directory squatting on the new file path makes both the copy and any rewrite fail.
        Directory.CreateDirectory(NewConfigPath);
        WriteConfig(LegacyConfigPath, new PrinterConfiguration
        {
            RestaurantName = "Legacy Kebab",
            ApiKey = "legacy-key-777", // pragma: allowlist secret (fake test value)
        });
        var legacyBytesBefore = File.ReadAllText(LegacyConfigPath);

        var config = await CreateStore().LoadAsync();

        Assert.Equal("Legacy Kebab", config.RestaurantName);
        Assert.Equal("legacy-key-777", config.ApiKey);
        // The legacy file must never be modified, even though the key migration was attempted.
        Assert.Equal(legacyBytesBefore, File.ReadAllText(LegacyConfigPath));
    }

    // --- API key migration (blank-after-verify) ---

    [Fact]
    public async Task Load_moves_plaintext_api_key_to_secret_store_and_blanks_new_file_only()
    {
        WriteConfig(LegacyConfigPath, new PrinterConfiguration
        {
            RestaurantName = "Rumi",
            ApiKey = "printer-key-alpha", // pragma: allowlist secret (fake test value)
        });
        var legacyBytesBefore = File.ReadAllText(LegacyConfigPath);

        var config = await CreateStore().LoadAsync();

        Assert.Equal("printer-key-alpha", config.ApiKey); // runtime keeps the effective key
        Assert.Equal("printer-key-alpha", _secrets.Peek(PrinterConfigurationStore.ApiKeySecretName));
        var persisted = ReadConfig(NewConfigPath);
        Assert.Equal(string.Empty, persisted.ApiKey);      // NEW copy blanked
        Assert.Equal("Rumi", persisted.RestaurantName);    // other fields preserved
        Assert.Equal(legacyBytesBefore, File.ReadAllText(LegacyConfigPath)); // OLD copy untouched
    }

    [Fact]
    public async Task Load_keeps_plaintext_file_when_secret_store_write_fails()
    {
        _secrets.FailWrites = true; // e.g. SecureStorage on unpackaged Windows
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiKey = "printer-key-beta" }); // pragma: allowlist secret (fake test value)
        var bytesBefore = File.ReadAllText(NewConfigPath);

        var config = await CreateStore().LoadAsync();

        Assert.Equal("printer-key-beta", config.ApiKey);
        Assert.Equal(bytesBefore, File.ReadAllText(NewConfigPath)); // status quo, byte-identical
    }

    [Fact]
    public async Task Load_keeps_plaintext_file_when_readback_verification_fails()
    {
        _secrets.CorruptOnWrite = true; // write "succeeds" but the read-back doesn't match
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiKey = "printer-key-gamma" }); // pragma: allowlist secret (fake test value)
        var bytesBefore = File.ReadAllText(NewConfigPath);

        var config = await CreateStore().LoadAsync();

        Assert.Equal("printer-key-gamma", config.ApiKey); // the file value, not the corrupted one
        Assert.Equal(bytesBefore, File.ReadAllText(NewConfigPath));
    }

    [Fact]
    public async Task Load_reads_api_key_from_secret_store_when_file_field_is_blank()
    {
        await _secrets.TrySetAsync(PrinterConfigurationStore.ApiKeySecretName, "stored-key-42");
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiKey = "" });

        var config = await CreateStore().LoadAsync();

        Assert.Equal("stored-key-42", config.ApiKey);
    }

    [Fact]
    public async Task Load_attempts_failing_key_migration_once_per_key_per_run()
    {
        // The polling loop reloads the config every 5 seconds; a permanently failing store
        // (unpackaged Windows) must not be hammered on every poll.
        _secrets.FailWrites = true;
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiKey = "printer-key-delta" }); // pragma: allowlist secret (fake test value)
        var store = CreateStore();

        await store.LoadAsync();
        await store.LoadAsync();

        Assert.Equal(1, _secrets.SetCalls);
    }

    [Fact]
    public async Task Load_migrates_rotated_key_written_into_the_file()
    {
        var store = CreateStore();
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiKey = "printer-key-v1" }); // pragma: allowlist secret (fake test value)
        await store.LoadAsync(); // migrates + blanks

        // Operator rotates the key by hand-editing the config file (there is no UI field).
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiKey = "printer-key-v2" }); // pragma: allowlist secret (fake test value)
        var config = await store.LoadAsync();

        Assert.Equal("printer-key-v2", config.ApiKey);
        Assert.Equal("printer-key-v2", _secrets.Peek(PrinterConfigurationStore.ApiKeySecretName));
        Assert.Equal(string.Empty, ReadConfig(NewConfigPath).ApiKey);
    }

    // --- Save ---

    [Fact]
    public async Task Save_blanks_api_key_in_file_when_secret_store_verifies()
    {
        var store = CreateStore();

        await store.SaveAsync(new PrinterConfiguration { RestaurantName = "Rumi", ApiKey = "printer-key-save" }); // pragma: allowlist secret (fake test value)

        Assert.Equal(string.Empty, ReadConfig(NewConfigPath).ApiKey);
        Assert.Equal("printer-key-save", _secrets.Peek(PrinterConfigurationStore.ApiKeySecretName));
        Assert.Equal("printer-key-save", (await store.LoadAsync()).ApiKey);
    }

    [Fact]
    public async Task Save_writes_plaintext_api_key_when_secret_store_unavailable()
    {
        _secrets.FailWrites = true;
        var store = CreateStore();

        await store.SaveAsync(new PrinterConfiguration { ApiKey = "printer-key-save" }); // pragma: allowlist secret (fake test value)

        Assert.Equal("printer-key-save", ReadConfig(NewConfigPath).ApiKey); // plaintext status quo
        Assert.Equal("printer-key-save", (await store.LoadAsync()).ApiKey);
    }

    [Fact]
    public async Task Save_then_load_round_trips_configuration_fields()
    {
        var store = CreateStore();
        var saved = new PrinterConfiguration
        {
            ApiBaseUrl = "https://staging.example.test",
            ApiKey = "printer-key-roundtrip", // pragma: allowlist secret (fake test value)
            KitchenPrinterName = "192.168.1.50:9100",
            KitchenPaperWidth = 58,
            EnableTimeRestriction = true,
            RestrictStartTime = new TimeSpan(11, 30, 0),
        };

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(saved.ApiBaseUrl, loaded.ApiBaseUrl);
        Assert.False(loaded.IsServiceRunning);
        Assert.True(FeedStartupPolicy.ShouldBeListening(loaded),
            "A saved tenant URL must preserve Android's automatic feed restart behavior.");
        Assert.Equal(saved.ApiKey, loaded.ApiKey);
        Assert.Equal(saved.KitchenPrinterName, loaded.KitchenPrinterName);
        Assert.Equal(saved.KitchenPaperWidth, loaded.KitchenPaperWidth);
        Assert.Equal(saved.EnableTimeRestriction, loaded.EnableTimeRestriction);
        Assert.Equal(saved.RestrictStartTime, loaded.RestrictStartTime);
    }

    [Fact]
    public async Task Save_then_load_round_trips_kitchen_routing_fields()
    {
        var store = CreateStore();
        var saved = new PrinterConfiguration
        {
            KitchenRoutingMode = KitchenRoutingMode.SingleKitchen,
            DefaultKitchenPrinterName = "192.168.1.50:9100",
            FrontKitchenPrinterName = "Front Queue",
            BackKitchenPrinterName = "file:/tmp/back-captures",
            KitchenPrinterName = "legacy-general",
        };

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(KitchenRoutingMode.SingleKitchen, loaded.KitchenRoutingMode);
        Assert.Equal(saved.DefaultKitchenPrinterName, loaded.DefaultKitchenPrinterName);
        Assert.Equal(saved.FrontKitchenPrinterName, loaded.FrontKitchenPrinterName);
        Assert.Equal(saved.BackKitchenPrinterName, loaded.BackKitchenPrinterName);
        Assert.Equal(saved.KitchenPrinterName, loaded.KitchenPrinterName);
    }

    // --- Existing behaviour preserved ---

    [Fact]
    public async Task Load_migrates_legacy_localhost_api_url_and_persists_it()
    {
        WriteConfig(NewConfigPath, new PrinterConfiguration { ApiBaseUrl = "http://localhost:5221" });

        var config = await CreateStore().LoadAsync();

        Assert.Equal("https://www.rumirestaurant.ch", config.ApiBaseUrl);
        Assert.Equal("https://www.rumirestaurant.ch", ReadConfig(NewConfigPath).ApiBaseUrl);
    }

    [Fact]
    public void ConfigFilePath_is_under_the_app_data_directory()
    {
        Assert.Equal(NewConfigPath, CreateStore().ConfigFilePath);
    }
}
