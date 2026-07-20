namespace PrinterAPP.Services;

/// <summary>
/// Outcome of a telemetry send, so the outbox knows whether to drop or retry a batch.
/// <c>Sent</c> = accepted (2xx). <c>Rejected</c> = permanently refused (4xx: bad batch / auth) — drop
/// it so one bad batch can't wedge the queue forever. <c>Retry</c> = transient (5xx / timeout / offline)
/// — keep and try again next cycle.
/// </summary>
public enum TelemetrySendResult
{
    Sent,
    Rejected,
    Retry
}
