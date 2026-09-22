using PrinterAPP.Models;

namespace PrinterAPP.Services;

public partial class OrderPipeline
{
    private async Task<IReadOnlyList<PrintAck>> BuildPrintAcksAsync(
        Order order,
        bool cashier,
        KitchenPrintOutcome frontKitchen,
        KitchenPrintOutcome backKitchen,
        KitchenPrintOutcome generalDefault,
        DateTime receivedAt)
    {
        var config = await _printerService.LoadConfigurationAsync();
        return order.RoutingStates is { Count: > 0 }
            ? TelemetryPayloads.PrintAcks(order, new TelemetryPayloads.PrintAckOutcomes(
                cashier, frontKitchen, backKitchen, generalDefault), config, receivedAt,
                _deviceIdentity.DeviceId)
            : TelemetryPayloads.PrintAcks(order, cashier, frontKitchen, backKitchen,
                config, receivedAt);
    }
}
