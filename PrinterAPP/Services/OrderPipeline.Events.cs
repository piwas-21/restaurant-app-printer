using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using Sentry;

namespace PrinterAPP.Services;

public partial class OrderPipeline
{
    // Isolate subscriber failures so one observer cannot turn a successful print into a pipeline
    // error or prevent the remaining observers from seeing the event.
    private void RaiseOrderProcessed(OrderProcessedEventArgs args)
    {
        var handler = OrderProcessed;
        if (handler is null)
            return;

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
