using PrinterAPP.Services;

namespace PrinterAPP.Tests;

/// <summary>
/// <see cref="IAppDataPathProvider"/> over throwaway temp directories — this test host has no
/// MAUI, so FileSystem.AppDataDirectory is not available (nor wanted).
/// </summary>
public sealed class FakeAppDataPathProvider : IAppDataPathProvider, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "printerapp-tests-" + Guid.NewGuid().ToString("N"));

    public FakeAppDataPathProvider()
    {
        AppDataDirectory = Path.Combine(_root, "new");
        LegacyAppDataDirectory = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(LegacyAppDataDirectory);
    }

    public string AppDataDirectory { get; }

    public string LegacyAppDataDirectory { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Leaked temp dirs are cleaned by the OS; never fail a test run over cleanup.
        }
    }
}

/// <summary>
/// In-memory <see cref="IFeedCursorStore"/>. Shared between two <c>EventStreamingService</c>
/// instances it stands in for "the same device across a process restart", which is the scenario the
/// cursor exists to survive.
/// </summary>
public sealed class InMemoryFeedCursorStore : IFeedCursorStore
{
    private PrinterAPP.Models.FeedCursor _cursor = new()
    {
        LastPollTime = DateTime.UtcNow - FeedCursorStore.MaxLookBack,
    };

    public int SaveCount { get; private set; }

    public PrinterAPP.Models.FeedCursor Load() => new()
    {
        LastPollTime = _cursor.LastPollTime,
        ProcessedOrders = new Dictionary<string, DateTime>(_cursor.ProcessedOrders),
    };

    public void Save(PrinterAPP.Models.FeedCursor cursor)
    {
        SaveCount++;
        _cursor = new PrinterAPP.Models.FeedCursor
        {
            LastPollTime = cursor.LastPollTime,
            ProcessedOrders = new Dictionary<string, DateTime>(cursor.ProcessedOrders),
        };
    }
}

/// <summary>In-memory <see cref="ISecretStore"/> with switchable failure modes.</summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();

    /// <summary>Simulates the unpackaged-Windows case: SecureStorage writes always fail.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Simulates a store whose reads fail (write appears to succeed, read-back returns null).</summary>
    public bool FailReads { get; set; }

    /// <summary>Simulates a store that accepts writes but corrupts them (read-back mismatch).</summary>
    public bool CorruptOnWrite { get; set; }

    public int GetCalls { get; private set; }

    public int SetCalls { get; private set; }

    public Task<string?> GetAsync(string name)
    {
        GetCalls++;
        if (FailReads)
            return Task.FromResult<string?>(null);
        return Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);
    }

    public Task<bool> TrySetAsync(string name, string value)
    {
        SetCalls++;
        if (FailWrites)
            return Task.FromResult(false);
        _values[name] = CorruptOnWrite ? value + "-corrupted" : value;
        return Task.FromResult(true);
    }

    /// <summary>Test-only direct read that doesn't count against <see cref="GetCalls"/>.</summary>
    public string? Peek(string name) => _values.TryGetValue(name, out var value) ? value : null;
}
