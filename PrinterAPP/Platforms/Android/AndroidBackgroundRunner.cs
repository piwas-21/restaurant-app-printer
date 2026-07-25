using Android.Content;
using Android.OS;
using Microsoft.Extensions.Logging;
using PrinterAPP.Services;

namespace PrinterAPP;

/// <summary>
/// Android implementation of <see cref="IBackgroundRunner"/>: starts <see
/// cref="OrderFeedForegroundService"/>, which is what actually keeps the order feed polling and
/// printing once the app is no longer the foreground UI.
/// </summary>
public class AndroidBackgroundRunner : IBackgroundRunner
{
    private readonly IOrderPipeline _pipeline;
    private readonly IFeedWatchdog _watchdog;
    private readonly ILogger<AndroidBackgroundRunner> _logger;

    public AndroidBackgroundRunner(
        IOrderPipeline pipeline, IFeedWatchdog watchdog, ILogger<AndroidBackgroundRunner> logger)
    {
        _pipeline = pipeline;
        _watchdog = watchdog;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            using var intent = new Intent(context, typeof(OrderFeedForegroundService));

            // Android 8+ requires startForegroundService for a service that will promote itself with
            // startForeground(); minSdk here is 24, so the older path still has to exist.
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception ex)
        {
            // Losing the service means losing background printing, but it must not stop the app from
            // running in the foreground — the pipeline is still initialised below.
            _logger.LogError(ex, "Failed to start the order-feed foreground service");
        }

        // Before InitializeAsync: the watchdog is idempotent, and starting it first means an init
        // failure is self-healing rather than terminal — its next tick starts the feed. Started here
        // rather than by the pipeline, which cannot own it without a dependency cycle.
        _watchdog.Start();

        // Also initialise directly so a foreground launch does not depend on service-start timing.
        // InitializeAsync is idempotent, so the service's own call is a no-op after this.
        await _pipeline.InitializeAsync(cancellationToken);
    }
}
