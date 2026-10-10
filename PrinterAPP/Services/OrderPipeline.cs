using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using Sentry;

namespace PrinterAPP.Services;

/// <inheritdoc cref="IOrderPipeline"/>
public partial class OrderPipeline : IOrderPipeline
{
    private readonly IEventStreamingService _feed;
    private readonly IOrderPrintService _orderPrintService;
    private readonly IOrderHistoryService _orderHistoryService;
    private readonly IPrintAckOutbox _printAckOutbox;
    private readonly IPrintUpdateJobStore? _updateJobStore;
    private readonly IPrinterUpdateAuthorizationService? _updateAuthorizationService;
    private readonly ITelemetryScheduler _telemetryScheduler;
    private readonly IPrinterService _printerService;
    private readonly IDeviceIdentityService _deviceIdentity;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<OrderPipeline> _logger;

    // A SemaphoreSlim rather than lock(): every transition awaits I/O (config load, feed start), and
    // you cannot await inside a monitor. Same "one owner at a time, never await holding it" rule
    // TelemetryScheduler follows, expressed with the primitive that supports awaiting.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    // Set by StopAsync, cleared by StartAsync. The watchdog must never undo a deliberate stop —
    // and it cannot infer intent from the config, because FeedStartupPolicy treats a configured
    // ApiBaseUrl as intent-to-listen on Android, which is true at launch but wrong here: it would
    // restart the feed seconds after a person tapped Stop. Deliberately not persisted, matching the
    // documented behaviour that a Stop tap lasts the session, not across restarts.
    private bool _stoppedOnPurpose;

    // When the current listening session began. Exposed for IFeedWatchdog, which uses it as the
    // grace-period reference until the feed reports its first successful poll.
    public DateTime FeedStartedAt { get; private set; } = DateTime.UtcNow;

    /// <inheritdoc />
    public bool StoppedOnPurpose => _stoppedOnPurpose;

    /// <inheritdoc />
    public async Task<bool> ShouldBeListeningAsync() =>
        FeedStartupPolicy.ShouldBeListening(await _printerService.LoadConfigurationAsync());

    // The feed is the single source of truth for running-ness; keeping a second bool here would let
    // the two disagree after an internal feed failure.
    public bool IsRunning => _feed.IsListening;

    public event EventHandler<OrderProcessedEventArgs>? OrderProcessed;

    public OrderPipeline(
        IEventStreamingService feed,
        IOrderPrintService orderPrintService,
        IOrderHistoryService orderHistoryService,
        IPrintAckOutbox printAckOutbox,
        ITelemetryScheduler telemetryScheduler,
        IPrinterService printerService,
        IDeviceIdentityService deviceIdentity,
        IRequestLogService requestLogService,
        ILogger<OrderPipeline> logger,
        IPrintUpdateJobStore? updateJobStore = null,
        IPrinterUpdateAuthorizationService? updateAuthorizationService = null)
    {
        _feed = feed;
        _orderPrintService = orderPrintService;
        _orderHistoryService = orderHistoryService;
        _printAckOutbox = printAckOutbox;
        _updateJobStore = updateJobStore;
        _updateAuthorizationService = updateAuthorizationService;
        _telemetryScheduler = telemetryScheduler;
        _printerService = printerService;
        _deviceIdentity = deviceIdentity;
        _requestLogService = requestLogService;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await _printerService.LoadConfigurationAsync();

            if (!_initialized)
            {
                // Tag fleet errors with this device + tenant. Done after config has loaded so the
                // lazily-generated device id persists. No-op when Sentry is inert.
                _deviceIdentity.ApplySentryTags(config.TenantSlug);

                // Heartbeats run for the life of the host process regardless of feed state, so a
                // stopped or wedged feed is still remotely visible.
                _telemetryScheduler.Start();
                _initialized = true;
            }

            if (FeedStartupPolicy.ShouldBeListening(config) && !_feed.IsListening)
            {
                await StartFeedAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stoppedOnPurpose = false;

            if (_feed.IsListening)
            {
                return;
            }

            await StartFeedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _stoppedOnPurpose = true;

            // The feed subscriptions stay attached: the feed raises nothing while stopped, and
            // keeping them means a restart is a plain StartListeningAsync with no re-wiring.
            await _feed.StopListeningAsync();
            await StopUpdateRetryLoopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stop-then-start under a single gate hold, without touching <see cref="_stoppedOnPurpose"/> —
    /// going through <see cref="StopAsync"/> would record the watchdog's own restart as a deliberate
    /// stop, and every later cycle would then decline to act.
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _feed.StopListeningAsync();
            await StartFeedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private async Task StartFeedAsync(CancellationToken cancellationToken)
    {
        SubscribeToFeed();
        FeedStartedAt = DateTime.UtcNow;
        await _feed.StartListeningAsync(cancellationToken);
        StartUpdateProcessing();
        _logger.LogInformation("Order pipeline started — feed listening");
    }


}
