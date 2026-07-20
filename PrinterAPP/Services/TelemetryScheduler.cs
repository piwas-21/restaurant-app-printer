using Microsoft.Extensions.Logging;

namespace PrinterAPP.Services;

/// <summary>
/// Periodic heartbeat driver. Each cycle loads current config, reads the device identity + live feed
/// state, and hands a composed <see cref="Models.HeartbeatRequest"/> to <see cref="ITelemetryClient"/>.
/// The loop never throws — every cycle is guarded so a transient failure just retries next tick.
/// </summary>
public class TelemetryScheduler : ITelemetryScheduler
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private readonly ITelemetryClient _client;
    private readonly IPrinterService _printerService;
    private readonly IDeviceIdentityService _identity;
    private readonly IEventStreamingService _feed;
    private readonly ILogger<TelemetryScheduler> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TelemetryScheduler(
        ITelemetryClient client,
        IPrinterService printerService,
        IDeviceIdentityService identity,
        IEventStreamingService feed,
        ILogger<TelemetryScheduler> logger)
    {
        _client = client;
        _printerService = printerService;
        _identity = identity;
        _feed = feed;
        _logger = logger;
    }

    public void Start()
    {
        // Locked so a stray concurrent Start can't spin up a second loop + leak the first's CTS.
        lock (_gate)
        {
            if (_loop is not null)
                return;

            _cts = new CancellationTokenSource();
            _loop = RunAsync(_cts.Token);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Report promptly on launch, then on the interval.
        await SendHeartbeatAsync(cancellationToken);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SendHeartbeatAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on StopAsync.
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await _printerService.LoadConfigurationAsync();
            var request = TelemetryPayloads.Heartbeat(
                config, _identity.Platform, _identity.AppVersion,
                _feed.IsListening, _feed.LastSuccessfulPollAt);

            await _client.SendHeartbeatAsync(
                request, config.ApiBaseUrl, config.ApiKey, _identity.DeviceId, cancellationToken);
        }
        catch (Exception ex)
        {
            // A telemetry cycle must never bring down the app; log and try again next tick.
            _logger.LogWarning(ex, "Heartbeat cycle failed");
        }
    }

    public async Task StopAsync()
    {
        // Capture + clear state under the lock, then await/dispose OUTSIDE it (never await holding a
        // lock). A concurrent Stop sees the nulled fields and no-ops; a concurrent Start sees them
        // cleared and starts fresh.
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
            return;

        cts.Cancel();
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
