using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
namespace PrinterAPP.Services;
/// <summary>
/// Atomic file-backed implementation of <see cref="IPrintUpdateJobStore"/>. A job is written before
/// the feed cursor is allowed to move. Processing entries are recovered as Unknown on load because
/// the process may have stopped after bytes left for the printer but before the outcome was stored.
/// </summary>
public partial class PrintUpdateJobStore : IUpdateJobStore
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
            var withdrawalKey = new PrintUpdateJobKey(update.JobId, 2, update.Target);
            if (!update.IsWithdrawn && records.TryGetValue(withdrawalKey, out var withdrawal))
            {
                record = withdrawal;
                shouldDispatch = false;
                return true;
            }

            if (records.TryGetValue(update.Key, out var existing))
            {
                record = existing;
                if (existing.Update.IsWithdrawn && update.Revision == 1)
                {
                    shouldDispatch = false;
                    return true;
                }
                // The identity is immutable: a changed duplicate is corruption, not a new job.
                shouldDispatch = false;
                if (!PrinterJsonSerialization.AreEquivalent(record.Update, update))
                    return false;
                shouldDispatch = record.IsPending;
                return true;
            }
            var before = new Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord>(records);
            if (update.IsWithdrawn)
                RedactOriginalRevision(records, update);

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
    public IReadOnlyList<PrintUpdateJobRecord> GetPendingFinalAcknowledgements()
    {
        lock (_gate)
        {
            return EnsureLoaded().Values
                .Where(record => !record.IsPending
                    && record.State != PrintUpdateJobState.Processing
                    && !record.FinalAcknowledgementQueued)
                .OrderBy(record => record.FirstSeenAt)
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
                FinalAcknowledgementQueued = false,
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

            records[key] = current with
            {
                State = state,
                FailureReason = failureReason,
                FinalAcknowledgementQueued = false,
            };
            if (SaveLocked())
                return true;

            // The outcome write failed after the print path. Do not retry bytes whose delivery may
            // have happened; retain an in-memory hold and recover the on-disk Processing record as
            // Unknown after restart.
            RestoreRecord(records, key, current with
            {
                State = PrintUpdateJobState.Unknown,
                FailureReason = "Could not persist the print outcome. Check the printer before sending COPY.",
                FinalAcknowledgementQueued = false,
            });
            return false;
        }
    }

    public bool MarkFinalAcknowledgementQueued(PrintUpdateJobKey key)
    {
        lock (_gate)
        {
            var records = EnsureLoaded();
            if (!records.TryGetValue(key, out var current)
                || current.State is PrintUpdateJobState.Pending or PrintUpdateJobState.Processing)
                return false;

            records[key] = current with { FinalAcknowledgementQueued = true };
            if (SaveLocked())
                return true;

            RestoreRecord(records, key, current with { FinalAcknowledgementQueued = false });
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

                // A process may have been killed after the printer received some or all bytes. That
                // is an ambiguous physical outcome: require an operator to inspect before COPY.
                var recovered = record.State == PrintUpdateJobState.Processing
                    ? record with
                    {
                        State = PrintUpdateJobState.Unknown,
                        FailureReason = "App restarted during printing. Check the printer before sending COPY.",
                        FinalAcknowledgementQueued = false,
                    }
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
