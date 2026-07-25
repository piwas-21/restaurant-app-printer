using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using Sentry;

namespace PrinterAPP.Services;

/// <inheritdoc cref="IOrderPipeline"/>
public class OrderPipeline : IOrderPipeline
{
    private readonly IEventStreamingService _feed;
    private readonly IOrderPrintService _orderPrintService;
    private readonly IOrderHistoryService _orderHistoryService;
    private readonly IPrintAckOutbox _printAckOutbox;
    private readonly ITelemetryScheduler _telemetryScheduler;
    private readonly IPrinterService _printerService;
    private readonly IDeviceIdentityService _deviceIdentity;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<OrderPipeline> _logger;

    // A SemaphoreSlim rather than lock(): every transition awaits I/O (config load, feed start), and
    // you cannot await inside a monitor. Same "one owner at a time, never await holding it" rule
    // TelemetryScheduler follows, expressed with the primitive that supports awaiting.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _subscribed;
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
        ILogger<OrderPipeline> logger)
    {
        _feed = feed;
        _orderPrintService = orderPrintService;
        _orderHistoryService = orderHistoryService;
        _printAckOutbox = printAckOutbox;
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
        await _gate.WaitAsync();
        try
        {
            _stoppedOnPurpose = true;

            // The OrderReceived subscription stays attached: the feed raises nothing while stopped,
            // and keeping it means a restart is a plain StartListeningAsync with no re-wiring.
            await _feed.StopListeningAsync();
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
        if (!_subscribed)
        {
            _feed.OrderReceived += OnOrderReceived;
            _subscribed = true;
        }

        FeedStartedAt = DateTime.UtcNow;
        await _feed.StartListeningAsync(cancellationToken);
        _logger.LogInformation("Order pipeline started — feed listening");
    }

    private void OnOrderReceived(object? sender, OrderEvent orderEvent)
    {
        // Deliberately not awaited, and deliberately NOT marshalled onto the UI thread. Printing is
        // network/spooler I/O that must not block the 5s poll loop that raised this, and the Android
        // foreground service handles orders with no Activity — so there is no UI to depend on.
        // OrderHistoryService and RequestLogService marshal their own bound-collection mutations
        // (OrderHistoryService.cs:38, :96), so the UI stays safe.
        _ = ProcessOrderAsync(orderEvent);
    }

    private async Task ProcessOrderAsync(OrderEvent orderEvent)
    {
        var order = orderEvent.Order;
        if (order is null)
        {
            return;
        }

        bool cashier, frontKitchen, backKitchen;

        // Phase 1 — everything up to and including the physical print. Only a failure in here means
        // the order did not print.
        try
        {
            _logger.LogInformation("Order received: #{OrderNumber} — {EventType}",
                order.OrderNumber, orderEvent.EventType);

            _orderHistoryService.AddOrder(orderEvent);

            (cashier, frontKitchen, backKitchen) =
                await _orderPrintService.PrintOrderToAllPrintersAsync(order);

            // The order has been through the printers, so its dedup entry may now be persisted.
            // Confirming only here — not when the feed dispatched it — is what stops a process kill
            // between dispatch and print permanently suppressing a ticket that never came out.
            _feed.ConfirmOrderHandled(order.OrderNumber);
        }
        catch (Exception ex)
        {
            // One bad order must never take the pipeline down — the feed keeps polling. Surfaced on
            // the Diagnostics page and to Sentry so a systematic failure is visible to the fleet.
            _logger.LogError(ex, "Error processing order {OrderNumber}", order.OrderNumber);
            _requestLogService.LogError(
                "Order Pipeline", $"Failed to process order {order.OrderNumber}", ex.Message);
            SentrySdk.CaptureException(ex);

            RaiseOrderProcessed(new OrderProcessedEventArgs
            {
                Order = order,
                Cashier = false,
                FrontKitchen = false,
                BackKitchen = false,
                Error = ex,
            });
            return;
        }

        // Phase 2 — bookkeeping. The receipts are already out of the printer, so a failure here must
        // never be reported as a print failure: it would contradict the history entry, put a false
        // print-failure on the fleet dashboard, and (before this split) raise OrderProcessed a second
        // time for the same order with fabricated all-false flags.
        try
        {
            // History carries a single kitchen flag, so both kitchens must have printed to call it
            // printed (matches the behaviour this replaced).
            _orderHistoryService.UpdatePrintStatus(order.Id, frontKitchen && backKitchen, cashier);

            // Queue per-target print acks for the fleet backend (durable outbox → served-vs-acked
            // missed-order reconciliation). Config is re-read rather than cached so a Save made
            // between orders is reflected in the ack.
            var config = await _printerService.LoadConfigurationAsync();
            await _printAckOutbox.EnqueueAsync(TelemetryPayloads.PrintAcks(
                order, cashier, frontKitchen, backKitchen, config, orderEvent.Timestamp));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-print bookkeeping failed for order {OrderNumber}", order.OrderNumber);
            _requestLogService.LogError(
                "Order Pipeline",
                $"Order {order.OrderNumber} printed, but recording it failed",
                ex.Message);
            SentrySdk.CaptureException(ex);
        }

        // Raised exactly once, always with the real per-printer outcome.
        RaiseOrderProcessed(new OrderProcessedEventArgs
        {
            Order = order,
            Cashier = cashier,
            FrontKitchen = frontKitchen,
            BackKitchen = backKitchen,
        });
    }

    // A throwing subscriber must not be able to reach the pipeline's own error handling and turn a
    // good print into a reported failure. Invoked one subscriber at a time via GetInvocationList
    // rather than a plain multicast call, because a plain call abandons the rest of the list at the
    // first throw — one broken subscriber would silently deprive all the others of the event.
    private void RaiseOrderProcessed(OrderProcessedEventArgs args)
    {
        var handler = OrderProcessed;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<OrderProcessedEventArgs>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An OrderProcessed subscriber threw");
                SentrySdk.CaptureException(ex);
            }
        }
    }
}
