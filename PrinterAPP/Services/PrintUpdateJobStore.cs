using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
namespace PrinterAPP.Services;
/// <summary>
/// Atomic file-backed implementation of <see cref="IPrintUpdateJobStore"/>. A job is written before
/// the feed cursor is allowed to move. Processing entries are recovered as Pending on load, so a
/// process kill between claiming and physical printing cannot lose the leaf work.
/// </summary>
public class PrintUpdateJobStore : IUpdateJobStore
{
    private const string FileName = "print-update-jobs.json";
    private const string CursorFileName = "print-update-cursor.json";
    private const int MaxStored = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord>? _records;
    private bool _cursorLoaded;
    private string? _updateCursor;
    public PrintUpdateJobStore(IAppDataPathProvider paths, ILogger<PrintUpdateJobStore> logger)
        : this(paths, (ILogger)logger)
    {
    }
    protected PrintUpdateJobStore(IAppDataPathProvider paths, ILogger logger)
    {
        _logger = logger;
        FilePath = Path.Combine(paths.AppDataDirectory, FileName);
        CursorFilePath = Path.Combine(paths.AppDataDirectory, CursorFileName);
    }
    public string FilePath { get; }
    /// <summary>Path of the cursor owned by this update store.</summary>
    public string CursorFilePath { get; }
    public bool AddOrGet(
        PrinterFeedUpdate update,
        out PrintUpdateJobRecord record,
        out bool shouldDispatch)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var records = EnsureLoaded();
            if (records.TryGetValue(update.Key, out var existing))
            {
                record = existing;
                // The identity is immutable: a changed duplicate is corruption, not a new job.
                shouldDispatch = false;
                if (!PrinterJsonSerialization.AreEquivalent(record.Update, update))
                    return false;
                shouldDispatch = record.IsPending;
                return true;
            }
            var before = new Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord>(records);
            record = new PrintUpdateJobRecord
            {
                Update = update,
                State = PrintUpdateJobState.Pending,
                FirstSeenAt = DateTime.UtcNow,
            };
            records[update.Key] = record;
            Trim(records);
            if (!SaveLocked())
            {
                Restore(records, before);
                shouldDispatch = false;
                return false;
            }
            shouldDispatch = true;
            return true;
        }
    }
    public IReadOnlyList<PrintUpdateJobRecord> GetPending()
    {
        lock (_gate)
        {
            return EnsureLoaded().Values
                .Where(record => record.IsPending)
                .OrderBy(record => record.Update.CreatedAt)
                .ThenBy(record => record.Update.JobId)
                .ThenBy(record => record.Update.Revision)
                .ThenBy(record => record.Update.Target)
                .ToList();
        }
    }
    public bool TryBegin(PrintUpdateJobKey key)
    {
        lock (_gate)
        {
            var records = EnsureLoaded();
            if (!records.TryGetValue(key, out var current) || !current.IsPending)
                return false;
            records[key] = current with
            {
                State = PrintUpdateJobState.Processing,
                LastAttemptAt = DateTime.UtcNow,
            };
            if (SaveLocked())
                return true;

            // A failed write must not leave an in-memory Processing record that blocks every retry.
            RestoreRecord(records, key, current);
            return false;
        }
    }

    public bool Complete(PrintUpdateJobKey key, PrintUpdateJobState state, string? failureReason = null)
    {
        lock (_gate)
        {
            var records = EnsureLoaded();
            if (!records.TryGetValue(key, out var current))
                return false;

            if (state == PrintUpdateJobState.Processing)
                throw new ArgumentException("A job cannot be completed as Processing.", nameof(state));

            records[key] = current with { State = state, FailureReason = failureReason };
            if (SaveLocked())
                return true;

            // Physical delivery is not known to be durable in the local history. Keep the job
            // retryable in this process; a restart also recovers the durable Processing record.
            RestoreRecord(records, key, current with
            {
                State = PrintUpdateJobState.Failed,
                FailureReason = failureReason ?? "Could not persist update-job outcome.",
            });
            return false;
        }
    }

    public IReadOnlyList<PrintUpdateJobRecord> GetHistory()
    {
        lock (_gate)
        {
            return EnsureLoaded().Values
                .OrderBy(record => record.FirstSeenAt)
                .ThenBy(record => record.Update.JobId)
                .ThenBy(record => record.Update.Revision)
                .ThenBy(record => record.Update.Target)
                .ToList();
        }
    }

    public string? LoadUpdateCursor()
    {
        lock (_gate)
        {
            EnsureCursorLoaded();
            return _updateCursor;
        }
    }

    public bool TryAdvanceUpdateCursor(string? cursor)
    {
        lock (_gate)
        {
            EnsureCursorLoaded();
            if (cursor is not null && string.IsNullOrWhiteSpace(cursor))
                return false;
            try
            {
                var directory = Path.GetDirectoryName(CursorFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var temp = CursorFilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cursor, JsonOptions));
                File.Move(temp, CursorFilePath, overwrite: true);
                _updateCursor = cursor;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist update-feed cursor");
                return false;
            }
        }
    }

    private Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> EnsureLoaded()
    {
        if (_records is not null)
            return _records;

        _records = new Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord>();
        try
        {
            if (!File.Exists(FilePath))
                return _records;

            var loaded = JsonSerializer.Deserialize<List<PrintUpdateJobRecord>>(
                File.ReadAllText(FilePath), JsonOptions);
            foreach (var record in loaded ?? Enumerable.Empty<PrintUpdateJobRecord>())
            {
                if (record.Update is null || record.Update.JobId == Guid.Empty || record.Update.Revision <= 0)
                    continue;

                // A process may have been killed while a job was owned. The durable truth is that it
                // was not completed, so make it eligible again rather than leaving a permanent wedge.
                var recovered = record.State == PrintUpdateJobState.Processing
                    ? record with { State = PrintUpdateJobState.Pending }
                    : record;
                _records[recovered.Key] = recovered;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update-job history unreadable; starting with an empty history");
        }

        return _records;
    }

    private void EnsureCursorLoaded()
    {
        if (_cursorLoaded)
            return;

        _cursorLoaded = true;
        try
        {
            if (File.Exists(CursorFilePath))
            {
                _updateCursor = JsonSerializer.Deserialize<string>(
                    File.ReadAllText(CursorFilePath), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update-feed cursor unreadable; starting from the initial boundary");
            _updateCursor = null;
        }
    }

    private static void Trim(Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> records)
    {
        if (records.Count <= MaxStored)
            return;

        // Never evict pending work: doing so while the feed cursor advances would lose a kitchen
        // leaf. Only old terminal history is bounded; if every entry is pending, retain all of it.
        var removable = records.Values
            .Where(record => !record.IsPending && record.State != PrintUpdateJobState.Processing)
            .OrderBy(record => record.FirstSeenAt)
            .Take(Math.Max(0, records.Count - MaxStored))
            .Select(record => record.Key)
            .ToList();
        foreach (var key in removable)
            records.Remove(key);
    }

    private bool SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temp = FilePath + ".tmp";
            var payload = JsonSerializer.Serialize(EnsureLoaded().Values.ToList(), JsonOptions);
            File.WriteAllText(temp, payload);
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist update-job history");
            return false;
        }
    }

    private static void RestoreRecord(
        Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> records,
        PrintUpdateJobKey key,
        PrintUpdateJobRecord record) => records[key] = record;

    private static void Restore(
        Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> target,
        Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> source)
    {
        target.Clear();
        foreach (var pair in source)
            target[pair.Key] = pair.Value;
    }
}
/// <summary>Compatibility implementation name used by integrations that call it UpdateJobStore.</summary>
public sealed class UpdateJobStore : PrintUpdateJobStore
{
    public UpdateJobStore(IAppDataPathProvider paths, ILogger<UpdateJobStore> logger)
        : base(paths, logger)
    {
    }
}
