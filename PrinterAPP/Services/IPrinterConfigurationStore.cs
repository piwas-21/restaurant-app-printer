using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Persistence for <see cref="PrinterConfiguration"/>: app-data file location, copy-only
/// migration from the legacy %APPDATA% location, and secret-store handling of the printer-feed
/// API key.
/// </summary>
public interface IPrinterConfigurationStore
{
    /// <summary>Absolute path of the active (new-location) config file, for display in the UI.</summary>
    string ConfigFilePath { get; }

    Task<PrinterConfiguration> LoadAsync();

    Task SaveAsync(PrinterConfiguration config);
}
