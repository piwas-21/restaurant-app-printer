using Microsoft.Maui.Storage;

namespace PrinterAPP.Services;

/// <summary>
/// Device implementation of <see cref="IAppDataPathProvider"/>: MAUI's per-app data directory
/// (cross-platform correct, notably on Android) plus the legacy roaming root used by
/// pre-migration Windows installs.
/// </summary>
public class MauiAppDataPathProvider : IAppDataPathProvider
{
    public string AppDataDirectory => FileSystem.AppDataDirectory;

    public string LegacyAppDataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}
