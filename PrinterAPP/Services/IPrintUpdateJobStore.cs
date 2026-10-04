using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Durable owner and history for additive update jobs. Feed delivery is at-least-once; this store is
/// the idempotency boundary that makes one job have one owner/copy, including across a restart.
/// </summary>
public interface IPrintUpdateJobStore
{
    /// <summary>
    /// Adds a new update durably. A duplicate returns the existing record and never overwrites its
    /// payload. <paramref name="shouldDispatch"/> is true for new or retryable jobs.
    /// </summary>
    bool AddOrGet(PrinterFeedUpdate update, out PrintUpdateJobRecord record, out bool shouldDispatch);

    /// <summary>Returns durable retryable jobs. Processing jobs are not returned in-process.</summary>
    IReadOnlyList<PrintUpdateJobRecord> GetPending();

    /// <summary>Returns terminal outcomes whose final acknowledgement is not yet durable in the outbox.</summary>
    IReadOnlyList<PrintUpdateJobRecord> GetPendingFinalAcknowledgements() => Array.Empty<PrintUpdateJobRecord>();

    /// <summary>Atomically gives one pipeline owner the job.</summary>
    bool TryBegin(PrintUpdateJobKey key);

    /// <summary>Records the outcome and persists the history entry.</summary>
    bool Complete(PrintUpdateJobKey key, PrintUpdateJobState state, string? failureReason = null);

    /// <summary>Marks a final outcome snapshot as durably queued after the outbox accepts it.</summary>
    bool MarkFinalAcknowledgementQueued(PrintUpdateJobKey key) => true;

    /// <summary>Returns all retained update history, oldest first.</summary>
    IReadOnlyList<PrintUpdateJobRecord> GetHistory();

    /// <summary>Loads the update-stream cursor owned by this store.</summary>
    string? LoadUpdateCursor() => null;

    /// <summary>
    /// Persists the update-stream cursor. The feed must call this only after every update on the page
    /// is durably represented by <see cref="AddOrGet"/>; a false result leaves the prior cursor valid.
    /// The default keeps compatibility with lightweight in-memory doubles.
    /// </summary>
    bool TryAdvanceUpdateCursor(string? cursor) => true;

    /// <summary>Path used by the durable implementation; useful for diagnostics and tests.</summary>
    string FilePath { get; }
}

/// <summary>Compatibility name for callers that describe the feature as an update-job store.</summary>
public interface IUpdateJobStore : IPrintUpdateJobStore
{
}
