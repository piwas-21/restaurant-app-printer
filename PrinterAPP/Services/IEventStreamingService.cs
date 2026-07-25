using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IEventStreamingService
{
    event EventHandler<OrderEvent>? OrderReceived;
    event EventHandler<string>? ConnectionStatusChanged;
    Task StartListeningAsync(CancellationToken cancellationToken = default);
    Task StopListeningAsync();
    bool IsListening { get; }

    /// <summary>
    /// Reports that an order has finished going through the print path, so its dedup entry may be
    /// persisted and suppress the order after a restart.
    /// <para>Marking and persisting are deliberately separate. An order is marked processed the
    /// instant it is dispatched, which stops it being handled twice in this session — but printing
    /// happens asynchronously afterwards, so persisting at that moment would mean an OOM kill
    /// between dispatch and print permanently suppresses a ticket that never came out. A silently
    /// missing kitchen ticket is worse than a duplicate one, so unconfirmed orders are left out of
    /// the cursor and re-driven on the next start.</para>
    /// </summary>
    void ConfirmOrderHandled(string orderNumber);

    /// <summary>UTC time of the last successful order-feed poll, or null if none yet. Distinguishes
    /// "polling healthily" from "listening but wedged" (e.g. the deserialisation-wedge class), which
    /// <see cref="IsListening"/> alone can't — surfaced in the fleet heartbeat.</summary>
    DateTime? LastSuccessfulPollAt { get; }
}
