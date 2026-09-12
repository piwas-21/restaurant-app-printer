namespace PrinterAPP.Models;

/// <summary>
/// Durable update history entry. Keeping the complete feed payload alongside its state makes a
/// restart independent of whether the backend has already advanced past the update cursor.
/// </summary>
public sealed record PrintUpdateJobRecord
{
    public required PrinterFeedUpdate Update { get; init; }
    public PrintUpdateJobState State { get; init; } = PrintUpdateJobState.Pending;
    public DateTime FirstSeenAt { get; init; }
    public DateTime? LastAttemptAt { get; init; }
    public string? FailureReason { get; init; }

    public PrintUpdateJobKey Key => Update.Key;

    /// <summary>States that may be retried after a transient or configuration failure.</summary>
    public bool IsPending => State is PrintUpdateJobState.Pending
        or PrintUpdateJobState.Failed
        or PrintUpdateJobState.NotConfigured
        or PrintUpdateJobState.Unknown;
}
