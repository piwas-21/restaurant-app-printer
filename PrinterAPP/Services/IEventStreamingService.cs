using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IEventStreamingService
{
    event EventHandler<OrderEvent>? OrderReceived;
    event EventHandler<string>? ConnectionStatusChanged;
    Task StartListeningAsync(CancellationToken cancellationToken = default);
    Task StopListeningAsync();
    bool IsListening { get; }

    /// <summary>UTC time of the last successful order-feed poll, or null if none yet. Distinguishes
    /// "polling healthily" from "listening but wedged" (e.g. the deserialisation-wedge class), which
    /// <see cref="IsListening"/> alone can't — surfaced in the fleet heartbeat.</summary>
    DateTime? LastSuccessfulPollAt { get; }
}
