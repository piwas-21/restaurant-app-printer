using PrinterAPP.Models;

namespace PrinterAPP.Services;

public static partial class TelemetryPayloads
{
    private static List<PrintAck> RoutedPrintAcks(
        Order order,
        Guid orderId,
        KitchenPrintOutcome cashier,
        KitchenPrintOutcome frontKitchen,
        KitchenPrintOutcome backKitchen,
        KitchenPrintOutcome generalDefault,
        PrinterConfiguration config,
        DateTime receivedAt,
        string deviceId)
    {
        if (!OrderRoutingStateValidation.TryValidate(order, out _))
            return new List<PrintAck>();

        var acks = new List<PrintAck>();
        foreach (var route in order.RoutingStates!
                     .Where(route => route.Status == DevicePrintStatus.Queued
                         && string.Equals(route.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            KitchenPrintOutcome? outcome = route.Target switch
            {
                DevicePrintTarget.Cashier => cashier,
                DevicePrintTarget.FrontKitchen => frontKitchen,
                DevicePrintTarget.BackKitchen => backKitchen,
                DevicePrintTarget.General or DevicePrintTarget.Default => generalDefault,
                _ => null,
            };
            if (outcome is null || outcome.Value.Status == KitchenPrintStatus.NoWork)
                continue;

            var ack = route.Target switch
            {
                DevicePrintTarget.Cashier => BuildAck(orderId, route.Target, outcome.Value.IsSuccess,
                    config.CashierPrinterName, config.CashierAutoPrint,
                    config.CashierPrintCopies, receivedAt),
                DevicePrintTarget.FrontKitchen => BuildAck(orderId, route.Target, outcome.Value.IsSuccess,
                    FirstNonBlank(config.FrontKitchenPrinterName, config.CashierPrinterName),
                    config.FrontKitchenAutoPrint, config.KitchenPrintCopies, receivedAt),
                DevicePrintTarget.BackKitchen => BuildAck(orderId, route.Target, outcome.Value.IsSuccess,
                    FirstNonBlank(config.BackKitchenPrinterName, config.KitchenPrinterName),
                    config.BackKitchenAutoPrint, config.KitchenPrintCopies, receivedAt),
                DevicePrintTarget.General or DevicePrintTarget.Default => BuildOutcomeAck(
                    orderId, route.Target, outcome.Value, receivedAt),
                _ => null,
            };
            if (ack is null)
                continue;

            ack.JobId = route.JobId;
            ack.Revision = route.Revision;
            ack.JobType = DevicePrintJobType.Order;
            acks.Add(ack);
        }

        return acks;
    }
}
