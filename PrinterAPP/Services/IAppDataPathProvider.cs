namespace PrinterAPP.Services;

/// <summary>
/// Supplies the roots for per-user app data so file-storage services (and their plain net10.0
/// unit tests, which cannot load MAUI) never touch FileSystem/Environment directly.
/// </summary>
public interface IAppDataPathProvider
{
    /// <summary>Current app-data root (MAUI FileSystem.AppDataDirectory on device).</summary>
    string AppDataDirectory { get; }

    /// <summary>
    /// Legacy pre-migration root (Environment.SpecialFolder.ApplicationData — %APPDATA% on
    /// Windows) where installs prior to the FileSystem.AppDataDirectory move kept their files.
    /// Treated as read-only: nothing may ever write beneath it (rollback path).
    /// </summary>
    string LegacyAppDataDirectory { get; }
}
