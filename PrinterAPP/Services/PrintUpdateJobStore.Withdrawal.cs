using PrinterAPP.Models;

namespace PrinterAPP.Services;

public partial class PrintUpdateJobStore
{
    private bool TryRefreshWithdrawal(
        Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> records,
        PrintUpdateJobRecord existing,
        PrinterFeedUpdate update,
        out PrintUpdateJobRecord refreshed)
    {
        refreshed = existing;
        if (existing.Key != update.Key
            || !existing.Update.IsWithdrawn
            || existing.Update.Revision != 2
            || !update.IsWithdrawn
            || update.Revision != 2
            || PrinterFeedUpdateValidation.Validate(existing.Update) is not null
            || PrinterFeedUpdateValidation.Validate(update) is not null
            || update.CreatedAt <= existing.Update.CreatedAt
            || !PrinterJsonSerialization.AreEquivalent(
                existing.Update with { CreatedAt = update.CreatedAt }, update))
        {
            return false;
        }

        var before = new Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord>(records);
        refreshed = existing with { Update = update };
        records[update.Key] = refreshed;
        // Erasure may restamp a previously withdrawn note. Reapply redaction so an older local
        // payload can never survive the newer tombstone in either memory or the durable cache.
        RedactOriginalRevision(records, update);
        if (SaveLocked())
            return true;

        Restore(records, before);
        refreshed = existing;
        return false;
    }

    public bool MarkWithdrawn(PrintUpdateJobKey key)
    {
        lock (_gate)
        {
            var records = EnsureLoaded();
            if (!records.TryGetValue(key, out var current))
                return false;

            var state = IsRetryable(current.State) ? PrintUpdateJobState.Withdrawn : current.State;
            records[key] = RedactRecord(current, state);
            if (SaveLocked())
                return true;

            RestoreRecord(records, key, current);
            return false;
        }
    }

    public bool IsWithdrawalRequested(PrintUpdateJobKey key)
    {
        lock (_gate)
        {
            return EnsureLoaded().TryGetValue(key, out var current) && current.Update.IsWithdrawn;
        }
    }

    private static void RedactOriginalRevision(
        Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> records,
        PrinterFeedUpdate withdrawal)
    {
        var originalKey = new PrintUpdateJobKey(withdrawal.JobId, 1, withdrawal.Target);
        if (!records.TryGetValue(originalKey, out var original))
            return;

        var state = IsRetryable(original.State) ? PrintUpdateJobState.Withdrawn : original.State;
        records[originalKey] = RedactRecord(original, state);
    }

    private static PrintUpdateJobRecord RedactRecord(
        PrintUpdateJobRecord record,
        PrintUpdateJobState state) => record with
    {
        Update = PrinterUpdateWithdrawalRedactor.Redact(record.Update),
        State = state,
        FailureReason = state == PrintUpdateJobState.Withdrawn
            ? "Withdrawn by the backend; no new print will be sent."
            : record.FailureReason,
        FinalAcknowledgementQueued = state == PrintUpdateJobState.Withdrawn
            ? false : record.FinalAcknowledgementQueued,
    };

    private static bool IsRetryable(PrintUpdateJobState state) => state is
        PrintUpdateJobState.Pending or PrintUpdateJobState.Failed or PrintUpdateJobState.NotConfigured;
}
