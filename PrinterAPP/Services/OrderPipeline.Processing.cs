using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using Sentry;
namespace PrinterAPP.Services;
/// <summary>Update-job ownership, retry, and order-processing halves of the headless pipeline.</summary>
public partial class OrderPipeline
{
    private CancellationTokenSource? _updateRetryCts;
    private Task? _updateRetryTask;
    private bool _subscribed;
    private bool _updateSubscribed;
    private void SubscribeToFeed()
    {
        if (!_subscribed)
        {
            _feed.OrderReceived += OnOrderReceived;
            _subscribed = true;
        }
        if (!_updateSubscribed)
        {
            _feed.UpdateReceived += OnUpdateReceived;
            _updateSubscribed = true;
        }
    }
    private void StartUpdateProcessing()
    {
        StartUpdateRetryLoop();
        // The update cursor may already be terminal after a previous process. Re-drive every
        // durable pending job explicitly; feed delivery is not the retry mechanism.
        if (_updateJobStore is not null)
        {
            foreach (var pending in _updateJobStore.GetPending())
                _ = ProcessUpdateAsync(pending.Update);
        }
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
    private void OnUpdateReceived(object? sender, PrinterFeedUpdate update)
    {
        // The feed stages the payload before raising this event. The job store claim below is still
        // required because a duplicate page and the restart drain can arrive at the same time.
        _ = ProcessUpdateAsync(update);
    }
    private async Task ProcessUpdateAsync(PrinterFeedUpdate update)
    {
        if (_updateJobStore is not null && !_updateJobStore.TryBegin(update.Key))
            return;
        var receivedAt = DateTime.UtcNow;
        try
        {
            // Queue first: if the process dies before physical printing, the lifecycle still records
            // that the job was durably owned. PrintAckOutbox coalesces later snapshots by job key.
            // TryBegin is the cancellation boundary. Once claimed, the retry scheduler may stop, but
            // physical output and durable lifecycle bookkeeping must finish without leaving Processing.
            await _printAckOutbox.EnqueueAsync(
                new[] { TelemetryPayloads.UpdateQueuedAck(update, receivedAt) }, CancellationToken.None);
            KitchenPrintOutcome outcome;
            try
            {
                outcome = await _orderPrintService.PrintUpdateAsync(update, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update job {JobId} failed during printing", update.JobId);
                outcome = KitchenPrintOutcome.Failed;
            }
            if (_updateJobStore is not null)
            {
                var state = outcome.Status switch
                {
                    KitchenPrintStatus.Sent => PrintUpdateJobState.Sent,
                    KitchenPrintStatus.Skipped => PrintUpdateJobState.Skipped,
                    KitchenPrintStatus.NotConfigured => PrintUpdateJobState.NotConfigured,
                    KitchenPrintStatus.Unknown => PrintUpdateJobState.Unknown,
                    _ => PrintUpdateJobState.Failed,
                };
                var completed = TryCompleteUpdate(
                    update.Key,
                    state,
                    state is PrintUpdateJobState.Failed
                        or PrintUpdateJobState.NotConfigured
                        or PrintUpdateJobState.Unknown
                        ? outcome.Status.ToString()
                        : null);
                if (!completed)
                {
                    // Do not acknowledge Sent when local history could not record it. The store has
                    // deliberately left the job retryable; a conservative Failed snapshot is the
                    // only truthful wire state until a later retry establishes a durable result.
                    await _printAckOutbox.EnqueueAsync(
                        new[] { TelemetryPayloads.UpdateAck(update, KitchenPrintOutcome.Failed, receivedAt) },
                        CancellationToken.None);
                    return;
                }
            }
            // Sent is the only successful update outcome. Failed, NotConfigured and Unknown stay
            // explicit; none is ever upgraded to Printed/Sent by the ack mapper.
            await _printAckOutbox.EnqueueAsync(
                new[] { TelemetryPayloads.UpdateAck(update, outcome, receivedAt) }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Keep the job retryable if a bookkeeping edge throws. A malformed route must not make
            // the update disappear merely because telemetry was unavailable.
            _logger.LogError(ex, "Update job {JobId} bookkeeping failed", update.JobId);
            TryCompleteUpdate(update.Key, PrintUpdateJobState.Failed, ex.Message);
        }
    }
    private bool TryCompleteUpdate(
        PrintUpdateJobKey key,
        PrintUpdateJobState state,
        string? failureReason)
    {
        if (_updateJobStore is null)
            return true;
        try
        {
            return _updateJobStore.Complete(key, state, failureReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist update job {JobId} outcome", key.JobId);
            return false;
        }
    }
    private void StartUpdateRetryLoop()
    {
        if (_updateJobStore is null || _updateRetryTask is not null)
            return;
        _updateRetryCts = new CancellationTokenSource();
        _updateRetryTask = RunUpdateRetryLoopAsync(_updateRetryCts.Token);
    }
    private async Task RunUpdateRetryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var updateJobStore = _updateJobStore;
            if (updateJobStore is null)
                return;
            var config = await _printerService.LoadConfigurationAsync();
            var seconds = Math.Max(
                PrinterConfiguration.MinimumUpdateRetryIntervalSeconds,
                config.UpdateRetryIntervalSeconds);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var pending in updateJobStore.GetPending())
                    _ = ProcessUpdateAsync(pending.Update);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the pipeline is deliberately stopped.
        }
    }
    private async Task StopUpdateRetryLoopAsync()
    {
        var cts = _updateRetryCts;
        var task = _updateRetryTask;
        _updateRetryCts = null;
        _updateRetryTask = null;
        if (cts is null)
            return;
        await cts.CancelAsync();
        if (task is not null)
            await task;
        cts.Dispose();
    }

    private async Task ProcessOrderAsync(OrderEvent orderEvent)
    {
        var order = orderEvent.Order;
        if (order is null)
        {
            return;
        }

        bool cashier;
        KitchenPrintOutcome frontKitchen, backKitchen, generalDefault;
        var routedOrder = order.RoutingStates is { Count: > 0 };
        var routedAckPersistenceAttempted = false;
        var routedAcksDurablyStored = false;

        try
        {
            _logger.LogInformation("Order received: #{OrderNumber} — {EventType}",
                order.OrderNumber, orderEvent.EventType);

            _orderHistoryService.AddOrder(orderEvent);

            (cashier, frontKitchen, backKitchen, generalDefault) =
                await _orderPrintService.PrintOrderToAllPrintersAsync(
                    order, cancellationToken: CancellationToken.None);

            // Routed confirmation requires durable final acks before advancing feed cursor/dedup.
            var canConfirm = OrderRoutingStateValidation.CanConfirm(
                order, _deviceIdentity.DeviceId, cashier, frontKitchen, backKitchen, generalDefault);
            if (canConfirm && routedOrder)
            {
                routedAckPersistenceAttempted = true;
                var finalAcks = await BuildPrintAcksAsync(
                    order, cashier, frontKitchen, backKitchen, generalDefault, orderEvent.Timestamp);
                if (finalAcks.Count == 0)
                {
                    throw new InvalidDataException(
                        $"Routed order {order.OrderNumber} produced no final printer acknowledgements.");
                }

                await _printAckOutbox.EnqueueAsync(finalAcks, CancellationToken.None);
                routedAcksDurablyStored = true;
            }

            if (canConfirm)
            {
                _feed.ConfirmOrderHandled(order.OrderNumber);
            }
            else
            {
                _feed.ReleaseOrderForRetry(order.OrderNumber, order.CreatedAt, order.UpdatedAt);
            }
        }
        catch (Exception ex)
        {
            var failureMessage = routedAckPersistenceAttempted
                ? $"Order {order.OrderNumber} printed, but durable routed acknowledgement persistence failed; "
                    + "physical output may have occurred and a retry may print duplicate paper."
                : $"Failed to process order {order.OrderNumber}";
            _logger.LogError(ex, "{FailureMessage}", failureMessage);
            _requestLogService.LogError(
                "Order Pipeline", failureMessage, ex.Message);
            SentrySdk.CaptureException(ex);
            _feed.ReleaseOrderForRetry(order.OrderNumber, order.CreatedAt, order.UpdatedAt);

            RaiseOrderProcessed(new OrderProcessedEventArgs
            {
                Order = order,
                Cashier = false,
                FrontKitchen = false,
                BackKitchen = false,
                GeneralDefault = KitchenPrintOutcome.Failed,
                Error = ex,
                AckPersistenceAmbiguous = routedAckPersistenceAttempted,
            });
            return;
        }

        try
        {
            _orderHistoryService.UpdatePrintStatus(
                order.Id,
                frontKitchen.IsSuccess && backKitchen.IsSuccess && generalDefault.IsSuccess,
                cashier);

            if (!routedAcksDurablyStored)
            {
                routedAckPersistenceAttempted |= routedOrder;
                await _printAckOutbox.EnqueueAsync(
                    await BuildPrintAcksAsync(order, cashier, frontKitchen, backKitchen, generalDefault,
                        orderEvent.Timestamp), CancellationToken.None);
                routedAcksDurablyStored = routedOrder;
            }
        }
        catch (Exception ex)
        {
            var failureMessage = routedAckPersistenceAttempted && !routedAcksDurablyStored
                ? $"Order {order.OrderNumber} printed, but durable routed acknowledgement persistence failed; "
                    + "physical output may have occurred and a retry may print duplicate paper."
                : $"Post-print bookkeeping failed for order {order.OrderNumber}";
            _logger.LogError(ex, "{FailureMessage}", failureMessage);
            _requestLogService.LogError(
                "Order Pipeline", failureMessage,
                ex.Message);
            SentrySdk.CaptureException(ex);
            if (routedAckPersistenceAttempted && !routedAcksDurablyStored)
                _feed.ReleaseOrderForRetry(order.OrderNumber, order.CreatedAt, order.UpdatedAt);
        }

        // Raised exactly once, always with the real per-printer outcome.
        RaiseOrderProcessed(new OrderProcessedEventArgs
        {
            Order = order,
            Cashier = cashier,
            FrontKitchen = frontKitchen,
            BackKitchen = backKitchen,
            GeneralDefault = generalDefault,
            AckPersistenceAmbiguous = routedAckPersistenceAttempted && !routedAcksDurablyStored,
        });
    }

}
