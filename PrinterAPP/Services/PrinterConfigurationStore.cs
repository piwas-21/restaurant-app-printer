using System.Text.Json;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Loads/saves <see cref="PrinterConfiguration"/> under the app-data root supplied by
/// <see cref="IAppDataPathProvider"/> and keeps the printer-feed API key in an
/// <see cref="ISecretStore"/> instead of the plaintext config file whenever the platform allows.
///
/// MAUI-free by design: the unit-test project (plain net10.0, no MAUI workload) links this file
/// and drives the migration state machine with fakes. Do not add MAUI API calls here.
///
/// Migration safety (live client machine):
/// - The legacy %APPDATA%/KitchenPrinter/config.json is only ever COPIED to the new location,
///   never moved, rewritten, or deleted — it stays behind as the rollback path.
/// - The API key is blanked in the NEW file only, and only after a successful secret-store
///   write has been verified by reading the value back. If the secret store is unavailable
///   (e.g. SecureStorage on unpackaged Windows), the plaintext file field keeps working exactly
///   as before.
/// </summary>
public class PrinterConfigurationStore : IPrinterConfigurationStore
{
    public const string ApiKeySecretName = "printer-feed-api-key"; // pragma: allowlist secret (storage key name, not a value)
    private const string ConfigFileName = "config.json";
    private const string LegacyConfigFolderName = "KitchenPrinter";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _configPath;
    private readonly string _legacyConfigPath;
    private readonly ISecretStore _secretStore;

    // Caches so the 5-second polling loop (which reloads the config on every poll) doesn't hit
    // platform secure storage — or re-attempt a failing key migration — every few seconds.
    // Intentionally lock-free although this singleton is hit concurrently (5 s poll, print paths,
    // UI save): all writes are idempotent and the store-before-blank ordering means the worst
    // interleaving is a redundant secret-store write or one transient default-read that the next
    // poll heals — never a lost or blanked key.
    private string? _verifiedSecretApiKey;
    private string? _migrationAttemptedForKey;

    public PrinterConfigurationStore(IAppDataPathProvider pathProvider, ISecretStore secretStore)
    {
        _configPath = Path.Combine(pathProvider.AppDataDirectory, ConfigFileName);
        _legacyConfigPath = Path.Combine(pathProvider.LegacyAppDataDirectory, LegacyConfigFolderName, ConfigFileName);
        _secretStore = secretStore;
    }

    public string ConfigFilePath => _configPath;

    public async Task<PrinterConfiguration> LoadAsync()
    {
        LegacyAppDataMigration.TryCopyLegacyFile(_legacyConfigPath, _configPath);

        var (config, sourcePath) = ReadConfigFile();

        // Only the new-location file may ever be rewritten. The legacy file is read here only
        // when the copy above failed, and must stay untouched (rollback path).
        var canRewriteFile = sourcePath is null || sourcePath == _configPath;

        var urlMigrated = MigrateLegacyApiUrl(config);
        var keyMigrated = await MigrateApiKeyToSecretStoreAsync(config);

        if (canRewriteFile && (urlMigrated || keyMigrated))
        {
            try
            {
                // WriteConfigFileAsync blanks the key because the secret store verifiably holds it.
                await WriteConfigFileAsync(config);
            }
            catch (Exception)
            {
                // Best-effort rewrite: on failure the previous file stays intact (plaintext
                // status quo, no data loss) and the next successful save persists the migration.
            }
        }

        return config;
    }

    public Task SaveAsync(PrinterConfiguration config) => WriteConfigFileAsync(config);

    /// <summary>
    /// Ensures <see cref="PrinterConfiguration.ApiKey"/> holds the effective key at runtime and
    /// moves a plaintext file key into the secret store when possible. Returns true only when
    /// the plaintext key was verifiably stored, i.e. the file copy is now safe to blank.
    /// </summary>
    private async Task<bool> MigrateApiKeyToSecretStoreAsync(PrinterConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            // Plaintext key in the file (initial install, secret-store-less fallback, or a
            // hand-edited rotation). Attempt the move once per key value per app run.
            if (_migrationAttemptedForKey == config.ApiKey)
                return false;
            _migrationAttemptedForKey = config.ApiKey;

            return await TryStoreApiKeySecurelyAsync(config.ApiKey);
        }

        // File field blank — the key lives in the secret store (normal post-migration state).
        _verifiedSecretApiKey ??= await _secretStore.GetAsync(ApiKeySecretName);
        if (!string.IsNullOrWhiteSpace(_verifiedSecretApiKey))
        {
            config.ApiKey = _verifiedSecretApiKey;
        }

        return false;
    }

    /// <summary>
    /// Writes the key to the secret store and verifies it by reading it back. Only a confirmed
    /// round-trip may justify removing the key from the config file.
    /// </summary>
    private async Task<bool> TryStoreApiKeySecurelyAsync(string apiKey)
    {
        if (_verifiedSecretApiKey == apiKey)
            return true; // already stored and verified during this run

        if (!await _secretStore.TrySetAsync(ApiKeySecretName, apiKey))
            return false;

        if (await _secretStore.GetAsync(ApiKeySecretName) != apiKey)
            return false;

        _verifiedSecretApiKey = apiKey;
        return true;
    }

    /// <summary>
    /// Writes the config to the new-location file. The API key is persisted blank when (and only
    /// when) the secret store verifiably holds it; otherwise it stays in the file — the
    /// pre-migration status quo (KNOWN fallback for unpackaged Windows, where SecureStorage
    /// has no package identity and always fails).
    /// </summary>
    private async Task WriteConfigFileAsync(PrinterConfiguration config)
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var persisted = config;
        if (!string.IsNullOrWhiteSpace(config.ApiKey) && await TryStoreApiKeySecurelyAsync(config.ApiKey))
        {
            persisted = Clone(config);
            persisted.ApiKey = string.Empty;
        }

        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(persisted, WriteOptions));
    }

    /// <summary>
    /// Reads the new-location file, falling back to the legacy file only when the new one is
    /// missing or unreadable (e.g. the copy migration failed). The legacy file is read-only.
    /// </summary>
    private (PrinterConfiguration Config, string? SourcePath) ReadConfigFile()
    {
        foreach (var path in new[] { _configPath, _legacyConfigPath })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var config = JsonSerializer.Deserialize<PrinterConfiguration>(File.ReadAllText(path));
                if (config != null)
                    return (config, path);
            }
            catch (Exception)
            {
                // Corrupt/unreadable candidate — try the next one; worst case we fall through to
                // defaults, matching the pre-refactor LoadConfigurationAsync behaviour.
            }
        }

        return (new PrinterConfiguration(), null);
    }

    private static bool MigrateLegacyApiUrl(PrinterConfiguration config)
    {
        // Early dev builds shipped with a localhost API URL; live installs are migrated to the
        // production domain. Preserved from the legacy WindowsPrinterService load path.
        if (config.ApiBaseUrl is "http://localhost:5221" or "https://localhost:5221")
        {
            config.ApiBaseUrl = "https://www.rumirestaurant.ch";
            return true;
        }

        return false;
    }

    private static PrinterConfiguration Clone(PrinterConfiguration config) =>
        JsonSerializer.Deserialize<PrinterConfiguration>(JsonSerializer.Serialize(config))!;
}
