using PrinterAPP.Models;

namespace PrinterAPP.Services;

public partial class PrintUpdateJobStore
{
    private static void Trim(Dictionary<PrintUpdateJobKey, PrintUpdateJobRecord> records)
    {
        if (records.Count <= MaxStored)
            return;

        // Keep retryable, ambiguous, and unacknowledged results for operator recovery.
        var removable = records.Values
            .Where(record => record.FinalAcknowledgementQueued
                && (record.State is PrintUpdateJobState.Sent
                    or PrintUpdateJobState.Skipped or PrintUpdateJobState.Withdrawn))
            .OrderBy(record => record.FirstSeenAt)
            .Take(Math.Max(0, records.Count - MaxStored))
            .Select(record => record.Key)
            .ToList();
        foreach (var key in removable)
            records.Remove(key);
    }
}
