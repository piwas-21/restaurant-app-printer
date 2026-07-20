using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Durable, at-least-once queue for print acknowledgements. Acks are persisted to disk so they
/// survive crashes and offline windows (unlike heartbeats, a missed ack shows as a false "missed
/// order" on the backend). Flushed periodically by the telemetry scheduler.
/// </summary>
public interface IPrintAckOutbox
{
    /// <summary>Persists acks to the queue. Best-effort — never throws.</summary>
    Task EnqueueAsync(IEnumerable<PrintAck> acks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the queued acks in backend-sized batches via <paramref name="sender"/>, removing each
    /// batch the sender accepts (returns <c>true</c>) and keeping the rest for the next cycle. The
    /// sender returns <c>false</c> for a transient failure, which stops the flush so ordering + the
    /// remaining backlog are preserved. Best-effort — never throws.
    /// </summary>
    Task FlushAsync(
        Func<IReadOnlyList<PrintAck>, Task<bool>> sender,
        CancellationToken cancellationToken = default);
}
