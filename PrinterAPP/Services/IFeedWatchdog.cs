namespace PrinterAPP.Services;

/// <summary>
/// Brings the order feed back when it has died or gone quiet without anyone noticing.
/// <para>Two distinct failures it covers. The feed's loop can end — after which nothing calls
/// <see cref="IOrderPipeline.InitializeAsync"/> again until the app is launched or the device
/// reboots, so a restaurant could go a whole service without orders. Or the loop can be alive but
/// stop completing polls, which is exactly the state <c>IsListening</c> alone cannot express and
/// <c>LastSuccessfulPollAt</c> was added to expose. The heartbeat already reports both to the fleet
/// dashboard; this acts on them on the device instead of waiting for somebody to read it.</para>
/// </summary>
public interface IFeedWatchdog
{
    /// <summary>Begins periodic health checks (idempotent — a second call while running is a no-op).</summary>
    void Start();

    /// <summary>Stops the checks and awaits the in-flight cycle.</summary>
    Task StopAsync();
}
