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
        // Falls back to when the feed started, so a feed that simply has not completed its first 5s
        // poll yet is not mistaken for a stalled one and restarted in a loop.
        var lastProgressAt = _feed.LastSuccessfulPollAt ?? _pipeline.FeedStartedAt;

        var action = FeedWatchdogDecision.Decide(
            _pipeline.StoppedOnPurpose, _feed.IsListening, lastProgressAt, DateTime.UtcNow, StaleThreshold);

        switch (action)
        {
            case FeedWatchdogAction.Start:
                _logger.LogWarning("Watchdog: the order feed is not listening — starting it");
                _requestLogService.LogWarning(
                    "Feed Watchdog", "The order feed had stopped and was restarted automatically");
                await _pipeline.StartAsync(cancellationToken);
                break;

            case FeedWatchdogAction.Restart:
                var age = $"{(DateTime.UtcNow - lastProgressAt).TotalMinutes:F1} min";
                _logger.LogWarning(
                    "Watchdog: feed listening but no progress for {Age} — restarting", age);
                _requestLogService.LogWarning(
                    "Feed Watchdog",
                    "The order feed stopped fetching orders and was restarted automatically",
                    $"No successful poll for {age}");
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
