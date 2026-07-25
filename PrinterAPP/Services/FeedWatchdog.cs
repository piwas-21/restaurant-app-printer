using Microsoft.Extensions.Logging;
using Sentry;

namespace PrinterAPP.Services;

/// <inheritdoc cref="IFeedWatchdog"/>
public class FeedWatchdog : IFeedWatchdog
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    // Generous against the 5s poll: only a feed that has produced nothing for this long is treated
    // as broken rather than merely unlucky with a flaky network.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(3);

    private readonly IOrderPipeline _pipeline;
    private readonly IEventStreamingService _feed;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<FeedWatchdog> _logger;

    // Same lock-and-never-await-under-it shape as TelemetryScheduler.
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public FeedWatchdog(
        IOrderPipeline pipeline,
        IEventStreamingService feed,
        IRequestLogService requestLogService,
        ILogger<FeedWatchdog> logger)
    {
        _pipeline = pipeline;
        _feed = feed;
        _requestLogService = requestLogService;
        _logger = logger;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = RunAsync(_cts.Token);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await CheckAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // The watchdog is the last line of defence; it must not be the thing that dies.
                    _logger.LogWarning(ex, "Feed watchdog cycle failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on StopAsync.
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var action = FeedWatchdogDecision.Decide(
            _pipeline.StoppedOnPurpose,
            await _pipeline.ShouldBeListeningAsync(),
            _feed.IsListening,
            _feed.LastSuccessfulPollAt,
            _pipeline.FeedStartedAt,
            DateTime.UtcNow,
            StaleThreshold);

        switch (action)
        {
            case FeedWatchdogAction.Start:
                _logger.LogWarning("Watchdog: the order feed is not listening — starting it");
                _requestLogService.LogWarning(
                    "Feed Watchdog", "The order feed had stopped and was restarted automatically");
                // InitializeAsync, not StartAsync: if the original init threw before it finished, the
                // heartbeat scheduler and Sentry identity tags never started either, and nothing else
                // retries them — restarting only the feed would bring printing back while leaving the
                // device invisible on the fleet dashboard. It is idempotent and gates the feed on the
                // same ShouldBeListening check made above.
                await _pipeline.InitializeAsync(cancellationToken);
                break;

            case FeedWatchdogAction.Restart:
                _logger.LogWarning("Watchdog: feed listening but not polling — restarting it");
                _requestLogService.LogWarning(
                    "Feed Watchdog",
                    "The order feed stopped fetching orders and was restarted automatically",
                    $"Last successful poll: {_feed.LastSuccessfulPollAt?.ToString("o") ?? "never"}");
                SentrySdk.CaptureMessage("Order feed stalled; watchdog restarted it", SentryLevel.Warning);
                await _pipeline.RestartAsync(cancellationToken);
                break;

            case FeedWatchdogAction.None:
            default:
                break;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        cts.Dispose();
    }
}
