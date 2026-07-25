namespace PrinterAPP.Services;

/// <summary>
/// Platform seam for keeping <see cref="IOrderPipeline"/> alive while the app is not the foreground
/// UI.
/// <para>On Android that requires a real foreground service: a backgrounded process is throttled by
/// Doze/App Standby, frozen by OEM battery managers and eventually reclaimed by the low-memory
/// killer — which is why orders stopped printing when staff switched apps. On Windows a desktop
/// process keeps running when minimised, so the runner just drives the pipeline in-process.</para>
/// </summary>
public interface IBackgroundRunner
{
    /// <summary>
    /// Ensures the platform host is up and the pipeline is initialised. Idempotent — safe to call on
    /// every app launch and every foreground-service start command.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}
