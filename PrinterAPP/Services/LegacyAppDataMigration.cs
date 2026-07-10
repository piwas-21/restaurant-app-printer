namespace PrinterAPP.Services;

/// <summary>
/// One-way, copy-only migration of per-user data files from their pre-MAUI locations
/// (Environment.SpecialFolder.ApplicationData subfolders) to FileSystem.AppDataDirectory.
///
/// The legacy file is NEVER moved, rewritten, or deleted: it stays behind as the rollback path
/// in case a release has to be rolled back on the live client machine to a build that still
/// reads the old location. MAUI-free so the unit-test project (plain net10.0) can link it.
/// </summary>
public static class LegacyAppDataMigration
{
    /// <summary>
    /// Copies <paramref name="legacyPath"/> to <paramref name="newPath"/> when the new file does
    /// not exist yet and the legacy one does. Returns true only when a copy actually happened.
    /// </summary>
    public static bool TryCopyLegacyFile(string legacyPath, string newPath)
    {
        try
        {
            if (File.Exists(newPath))
                return false; // already migrated (or a fresh install already saved) — new file wins

            if (!File.Exists(legacyPath))
                return false; // nothing to migrate (fresh install)

            var directory = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.Copy(legacyPath, newPath, overwrite: false);
            return true;
        }
        catch (Exception)
        {
            // Migration must never take the app down on the live client machine. On failure the
            // caller keeps reading the legacy location directly, so nothing is lost.
            return false;
        }
    }
}
