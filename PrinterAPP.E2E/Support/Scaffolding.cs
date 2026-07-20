using PrinterAPP.Services;

namespace PrinterAPP.E2E.Support;

/// <summary>
/// Test-only edges (docs/E2E-STRATEGY.md §No mocks of our own code): a temp file root for the durable
/// outbox — NOT a mock of the services under test (TelemetryClient / PrintAckOutbox run as the real
/// shipping code).
/// </summary>

/// <summary>Temp app-data root for the durable outbox file (disposed at test end).</summary>
public sealed class TempPaths : IAppDataPathProvider, IDisposable
{
    public string AppDataDirectory { get; }
    public string LegacyAppDataDirectory => AppDataDirectory;

    public TempPaths()
    {
        AppDataDirectory = Path.Combine(Path.GetTempPath(), "printerapp-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AppDataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(AppDataDirectory, recursive: true); } catch { /* best-effort */ }
    }
}
