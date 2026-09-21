using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class OrderRoutingStateValidationTests
{
    [Fact]
    public void LegacyOrder_WithUnknownKitchenOutcome_IsNotConfirmable()
    {
        var order = new Order { OrderNumber = "LEGACY-UNKNOWN" };

        Assert.False(OrderRoutingStateValidation.CanConfirm(
            order, "device-a", cashier: true, KitchenPrintOutcome.Unknown,
            KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent));
        Assert.False(OrderRoutingStateValidation.CanConfirm(
            order, "device-a", cashier: true, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Unknown, KitchenPrintOutcome.Sent));
        Assert.False(OrderRoutingStateValidation.CanConfirm(
            order, "device-a", cashier: true, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Sent, KitchenPrintOutcome.Unknown));
    }

    [Fact]
    public void LegacyOrder_WithKnownKitchenOutcomes_RemainsConfirmable()
    {
        var order = new Order { OrderNumber = "LEGACY-KNOWN" };

        Assert.True(OrderRoutingStateValidation.CanConfirm(
            order, "device-a", cashier: true, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Skipped, KitchenPrintOutcome.NoWork));
    }
}
