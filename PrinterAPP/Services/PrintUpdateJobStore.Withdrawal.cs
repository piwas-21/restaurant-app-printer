using PrinterAPP.Models;

namespace PrinterAPP.Services;

public partial class PrintUpdateJobStore
{
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
