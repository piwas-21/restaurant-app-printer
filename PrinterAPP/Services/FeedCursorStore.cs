using System.Text.Json;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// File-backed <see cref="IFeedCursorStore"/> under the app-data root from
/// <see cref="IAppDataPathProvider"/>.
///
/// MAUI-free by design, like <see cref="PrinterConfigurationStore"/>: the unit-test project (plain
/// net10.0, no MAUI workload) links this file. Do not add MAUI API calls here.
/// </summary>
public class FeedCursorStore : IFeedCursorStore
{
    private const string CursorFileName = "feed-cursor.json";

    /// <summary>
    /// Ceiling on how far back a restored cursor may reach. Must stay comfortably below the feed's
    /// dedup window (1 hour): the dedup set is what stops a re-fetched order printing twice, so the
    /// look-back can never be allowed to exceed the age of the entries guarding it. A cursor older
    /// than this (long outage, tablet off overnight) is clamped — those orders are stale anyway and
    /// re-printing a whole night of tickets would be worse than missing them.
    /// </summary>
    public static readonly TimeSpan MaxLookBack = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _cursorPath;
    private readonly object _fileLock = new();

    public FeedCursorStore(IAppDataPathProvider pathProvider)
    {
        _cursorPath = Path.Combine(pathProvider.AppDataDirectory, CursorFileName);
    }

    public string CursorFilePath => _cursorPath;

    public FeedCursor Load()
    {
        try
        {
            lock (_fileLock)
            {
                if (!File.Exists(_cursorPath))
                {
                    return NewCursor();
                }

                var cursor = JsonSerializer.Deserialize<FeedCursor>(File.ReadAllText(_cursorPath));
                if (cursor is null)
                {
                    return NewCursor();
                }

                cursor.ProcessedOrders ??= new Dictionary<string, DateTime>();

                // Clamp forwards only. A persisted cursor is never trusted to reach further back
                // than MaxLookBack, and a cursor somehow in the future would stall the feed
                // entirely, so it is pulled back to now.
                var earliest = DateTime.UtcNow - MaxLookBack;
                if (cursor.LastPollTime < earliest || cursor.LastPollTime > DateTime.UtcNow)
                {
                    cursor.LastPollTime = earliest;
                }

                return cursor;
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable (partial write, clock nonsense, permissions). Falling back to a
            // fresh cursor re-fetches the default window; the dedup set is empty so some orders may
            // reprint — strictly better than a feed that will not start.
            return NewCursor();
        }
    }

    public void Save(FeedCursor cursor) => TrySave(cursor);

    /// <summary>
    /// Writes the cursor and reports the outcome so callers can keep their in-memory position behind
    /// the durable one when the filesystem is unavailable. The legacy <see cref="Save"/> method stays
    /// best-effort for existing callers.
    /// </summary>
    public bool TrySave(FeedCursor cursor)
    {
        try
        {
            lock (_fileLock)
            {
                var directory = Path.GetDirectoryName(_cursorPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write-then-replace: a crash midway through a direct overwrite would leave a
                // truncated file, and this is written far more often than the config is.
                var temp = _cursorPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cursor, WriteOptions));
                File.Move(temp, _cursorPath, overwrite: true);
                return true;
            }
        }
        catch (Exception)
        {
            // Best-effort: a cursor we could not persist costs a re-fetch on the next restart, which
            // the dedup set absorbs. It must never interrupt polling or printing.
            return false;
        }
    }

    private static FeedCursor NewCursor() => new()
    {
        LastPollTime = DateTime.UtcNow - MaxLookBack,
        ProcessedOrders = new Dictionary<string, DateTime>(),
    };
}
