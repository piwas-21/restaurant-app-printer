namespace PrinterAPP.Services;

/// <summary>
/// Drives periodic fleet telemetry (heartbeats) in the background. Started once at app launch;
/// keeps reporting the device's liveness + feed state even when the order feed is stopped — which is
/// exactly the visibility the 2026-07-19 incident lacked.
/// </summary>
public interface ITelemetryScheduler
{
    /// <summary>Begins the periodic loop (idempotent — a second call while running is a no-op).</summary>
    void Start();

    /// <summary>Stops the loop and awaits the in-flight cycle.</summary>
    Task StopAsync();
}
