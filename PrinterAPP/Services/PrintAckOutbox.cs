using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// File-backed <see cref="IPrintAckOutbox"/>. MAUI-free (source-linked into the test project): the
/// only platform seam is <see cref="IAppDataPathProvider"/> for the file location. A single
/// <see cref="SemaphoreSlim"/> serialises reads/writes and the flush, so concurrent enqueue (from the
/// print path) and flush (from the scheduler) can't corrupt the file. Best-effort throughout —
/// telemetry must never break printing.
/// </summary>
public class PrintAckOutbox : IPrintAckOutbox
{
    private const string FileName = "print-ack-outbox.json";
    private const int MaxBatch = 500;     // backend RecordPrintAcksCommand per-request cap.
    private const int MaxStored = 2000;   // bound the file if we stay offline; drop oldest beyond this.

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppDataPathProvider _paths;
    private readonly ILogger<PrintAckOutbox> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PrintAckOutbox(IAppDataPathProvider paths, ILogger<PrintAckOutbox> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task EnqueueAsync(IEnumerable<PrintAck> acks, CancellationToken cancellationToken = default)
    {
        var toAdd = acks?.ToList() ?? new List<PrintAck>();
        if (toAdd.Count == 0)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = Load();
            foreach (var ack in toAdd)
            {
                // Update lifecycle acks are snapshots of one stable job, not independent work. Replace
                // a queued/failed snapshot with the newest state so an offline retry cannot send a
                // contradictory sequence or grow the file on every duplicate feed page.
                var existingIndex = pending.FindIndex(existing => SameUpdate(existing, ack));
                if (existingIndex >= 0)
                    pending[existingIndex] = ack;
                else
                    pending.Add(ack);
            }
            // Keep the newest MaxStored if a long offline window overflowed the queue.
            if (pending.Count > MaxStored)
                pending = pending.Skip(pending.Count - MaxStored).ToList();
            Save(pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue print acks");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(
        Func<IReadOnlyList<PrintAck>, Task<bool>> sender,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = Load();
            if (pending.Count == 0)
                return;

            var index = 0;
            while (index < pending.Count && !cancellationToken.IsCancellationRequested)
            {
                var batch = pending.Skip(index).Take(MaxBatch).ToList();

                bool remove;
                try
                {
                    remove = await sender(batch);
                }
                catch
                {
                    remove = false;   // treat a thrown sender as transient — keep the batch.
                }

                if (!remove)
                    break;   // transient — stop; this batch + the rest stay for next cycle.

                index += batch.Count;
            }

            // Persist whatever wasn't accepted (index..end); an empty remainder clears the file.
            Save(pending.Skip(index).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush print acks");
        }
        finally
        {
            _gate.Release();
        }
    }

    private string FilePath => Path.Combine(_paths.AppDataDirectory, FileName);

    private static bool SameUpdate(PrintAck left, PrintAck right) =>
        left.JobId.HasValue && left.Revision.HasValue && left.JobType.HasValue
        && right.JobId.HasValue && right.Revision.HasValue && right.JobType.HasValue
        && left.JobId == right.JobId
        && left.Revision == right.Revision
        && left.JobType == right.JobType
        && left.OrderId == right.OrderId
        && left.Target == right.Target;

    private List<PrintAck> Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new List<PrintAck>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<PrintAck>>(json, JsonOptions) ?? new List<PrintAck>();
        }
        catch (Exception ex)
        {
            // A corrupt/unreadable file must not wedge telemetry — start fresh.
            _logger.LogWarning(ex, "Print-ack outbox unreadable — resetting");
            return new List<PrintAck>();
        }
    }

    private void Save(List<PrintAck> acks)
    {
        var json = JsonSerializer.Serialize(acks, JsonOptions);
        // Write to a temp file then atomically replace, so a crash mid-write can't corrupt (and lose)
        // the durable queue — the whole point of persisting acks.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
