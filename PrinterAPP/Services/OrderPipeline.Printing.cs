using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using Sentry;

namespace PrinterAPP.Services;

public partial class OrderPipeline
{
    private async Task<OrderProcessingResult?> TryPrintAndConfirmAsync(
        Order order,
        OrderEvent orderEvent)
    {
        var routedOrder = order.RoutingStates is { Count: > 0 };
        var routedAckPersistenceAttempted = false;
        var routedAcksDurablyStored = false;

        try
        {
            _logger.LogInformation("Order received: #{OrderNumber} — {EventType}",
                order.OrderNumber, orderEvent.EventType);
            _orderHistoryService.AddOrder(orderEvent);
            var outcomes = await _orderPrintService.PrintOrderToAllPrintersAsync(
                order, cancellationToken: CancellationToken.None);
            var canConfirm = OrderRoutingStateValidation.CanConfirm(
                order, _deviceIdentity.DeviceId, outcomes.Cashier, outcomes.FrontKitchen,
                outcomes.BackKitchen, outcomes.GeneralDefault);

            if (canConfirm && routedOrder)
            {
                routedAckPersistenceAttempted = true;
                var finalAcks = await BuildPrintAcksAsync(
                    order, outcomes.Cashier, outcomes.FrontKitchen, outcomes.BackKitchen,
                    outcomes.GeneralDefault, orderEvent.Timestamp);
                if (finalAcks.Count == 0)
                    throw new InvalidDataException(
                        $"Routed order {order.OrderNumber} produced no final printer acknowledgements.");

                await _printAckOutbox.EnqueueAsync(finalAcks, CancellationToken.None);
                routedAcksDurablyStored = true;
            }

            if (canConfirm)
                _feed.ConfirmOrderHandled(order.OrderNumber);
            else
                _feed.ReleaseOrderForRetry(order.OrderNumber, order.CreatedAt, order.UpdatedAt);

            return new OrderProcessingResult(
                outcomes.Cashier, outcomes.FrontKitchen, outcomes.BackKitchen, outcomes.GeneralDefault,
                routedOrder, routedAckPersistenceAttempted, routedAcksDurablyStored);
        }
        catch (Exception ex)
        {
            var failureMessage = routedAckPersistenceAttempted
                ? $"Order {order.OrderNumber} printed, but durable routed acknowledgement persistence failed; "
                    + "physical output may have occurred and a retry may print duplicate paper."
                : $"Failed to process order {order.OrderNumber}";
            _logger.LogError(ex, "{FailureMessage}", failureMessage);
            _requestLogService.LogError("Order Pipeline", failureMessage, ex.Message);
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
            return null;
        }
    }

    private sealed record OrderProcessingResult(
        bool Cashier,
        KitchenPrintOutcome FrontKitchen,
        KitchenPrintOutcome BackKitchen,
        KitchenPrintOutcome GeneralDefault,
        bool RoutedOrder,
        bool RoutedAckPersistenceAttempted,
        bool RoutedAcksDurablyStored);
}
