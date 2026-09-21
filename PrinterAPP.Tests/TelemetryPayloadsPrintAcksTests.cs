using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class TelemetryPayloadsPrintAcksTests
{
    private static Order OrderWithId(string id) => new() { Id = id, OrderNumber = "ORD-1" };

    private static PrintAck AckFor(List<PrintAck> acks, DevicePrintTarget t) =>
        acks.Single(a => a.Target == t);

    [Fact]
    public void PrintAcks_ConfiguredAndPrinted_MapsToPrinted()
    {
        var orderId = Guid.NewGuid();
        var config = new PrinterConfiguration
        {
            CashierPrinterName = "192.168.1.51",
            FrontKitchenPrinterName = "192.168.1.50",
            BackKitchenPrinterName = "192.168.1.60",
            CashierPrintCopies = 2,
            KitchenPrintCopies = 1,
        };

        var acks = TelemetryPayloads.PrintAcks(
            OrderWithId(orderId.ToString()), cashier: true, frontKitchen: true, backKitchen: true,
            config, DateTime.UtcNow);

        Assert.Equal(3, acks.Count);
        var cashier = AckFor(acks, DevicePrintTarget.Cashier);
        Assert.Equal(DevicePrintStatus.Printed, cashier.Status);
        Assert.Equal(2, cashier.Copies);
        Assert.NotNull(cashier.PrintedAt);
        Assert.All(acks, a => Assert.Equal(orderId, a.OrderId));
    }

    [Fact]
    public void PrintAcks_ConfiguredButFailed_MapsToFailed()
    {
        var config = new PrinterConfiguration { CashierPrinterName = "192.168.1.51" };

        var acks = TelemetryPayloads.PrintAcks(
            OrderWithId(Guid.NewGuid().ToString()), cashier: false, frontKitchen: false, backKitchen: false,
            config, DateTime.UtcNow);

        var cashier = AckFor(acks, DevicePrintTarget.Cashier);
        Assert.Equal(DevicePrintStatus.Failed, cashier.Status);
        Assert.Null(cashier.PrintedAt);
        Assert.NotNull(cashier.FailureReason);
        Assert.Equal(0, cashier.Copies);
    }

    [Fact]
    public void PrintAcks_NoPrinterConfigured_MapsToSkipped()
    {
        // Back kitchen has no printer and no legacy fallback → Skipped, regardless of the bool.
        var config = new PrinterConfiguration { BackKitchenPrinterName = "", KitchenPrinterName = "" };

        var acks = TelemetryPayloads.PrintAcks(
            OrderWithId(Guid.NewGuid().ToString()), cashier: true, frontKitchen: true, backKitchen: true,
            config, DateTime.UtcNow);

        Assert.Equal(DevicePrintStatus.Skipped, AckFor(acks, DevicePrintTarget.BackKitchen).Status);
    }

    [Fact]
    public void PrintAcks_FrontKitchenFallsBackToCashierPrinter()
    {
        // Mirrors OrderPrintService: front kitchen with no dedicated printer uses the cashier printer.
        var config = new PrinterConfiguration { FrontKitchenPrinterName = "", CashierPrinterName = "192.168.1.51" };

        var acks = TelemetryPayloads.PrintAcks(
            OrderWithId(Guid.NewGuid().ToString()), cashier: true, frontKitchen: true, backKitchen: false,
            config, DateTime.UtcNow);

        Assert.Equal(DevicePrintStatus.Printed, AckFor(acks, DevicePrintTarget.FrontKitchen).Status);
    }

    [Fact]
    public void PrintAcks_AutoPrintDisabled_MapsToSkipped()
    {
        // Printer configured but auto-print off → the device deliberately didn't print → Skipped,
        // not a fabricated Printed (which would hide it on the fleet dashboard).
        var config = new PrinterConfiguration { CashierPrinterName = "192.168.1.51", CashierAutoPrint = false };

        var acks = TelemetryPayloads.PrintAcks(
            OrderWithId(Guid.NewGuid().ToString()), cashier: true, frontKitchen: false, backKitchen: false,
            config, DateTime.UtcNow);

        var cashier = AckFor(acks, DevicePrintTarget.Cashier);
        Assert.Equal(DevicePrintStatus.Skipped, cashier.Status);
        Assert.Null(cashier.PrintedAt);
        Assert.Equal(0, cashier.Copies);
    }

    [Fact]
    public void PrintAcks_NonGuidOrderId_ReturnsEmpty()
    {
        var acks = TelemetryPayloads.PrintAcks(
            OrderWithId("not-a-guid"), true, true, true, new PrinterConfiguration(), DateTime.UtcNow);

        Assert.Empty(acks);   // without a real id the backend can't reconcile — don't send noise
    }

    [Fact]
    public void RoutedPrintAcks_OnlyAcknowledgesThisDeviceWithStableJobIdentity()
    {
        var orderId = Guid.NewGuid();
        var frontJobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var order = OrderWithId(orderId.ToString());
        order.RoutingStates = new List<OrderRoutingState>
        {
            new()
            {
                Id = Guid.NewGuid(), JobId = Guid.NewGuid(), Revision = 1,
                Target = DevicePrintTarget.Cashier, DeviceId = "front-device",
                Status = DevicePrintStatus.Queued, Version = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), JobId = frontJobId, Revision = 2,
                Target = DevicePrintTarget.FrontKitchen, DeviceId = "front-device",
                Status = DevicePrintStatus.Queued, Version = 3,
            },
            new()
            {
                Id = Guid.NewGuid(), JobId = otherJobId, Revision = 1,
                Target = DevicePrintTarget.BackKitchen, DeviceId = "back-device",
                Status = DevicePrintStatus.Queued, Version = 1,
            },
            new()
            {
                Id = Guid.NewGuid(), JobId = Guid.NewGuid(), Revision = 1,
                Target = DevicePrintTarget.General, DeviceId = "front-device",
                Status = DevicePrintStatus.Printed, Version = 2,
            },
        };

        var acks = TelemetryPayloads.PrintAcks(
            order, cashier: true, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Sent,
            new PrinterConfiguration { CashierPrinterName = "cashier", FrontKitchenPrinterName = "front" },
            DateTime.UtcNow,
            "front-device");

        Assert.Equal(2, acks.Count);
        var frontAck = Assert.Single(acks, ack => ack.Target == DevicePrintTarget.FrontKitchen);
        Assert.Equal(frontJobId, frontAck.JobId);
        Assert.Equal(2, frontAck.Revision);
        Assert.Equal(DevicePrintJobType.Order, frontAck.JobType);
        Assert.Equal(DevicePrintStatus.Printed, frontAck.Status);
        var cashierAck = Assert.Single(acks, ack => ack.Target == DevicePrintTarget.Cashier);
        Assert.Equal(DevicePrintStatus.Printed, cashierAck.Status);
    }

    [Fact]
    public void RoutedPrintAcks_MalformedOrTerminalRoutes_AreNotAcknowledged()
    {
        var order = OrderWithId(Guid.NewGuid().ToString());
        order.RoutingStates =
        [
            new()
            {
                JobId = Guid.NewGuid(), Revision = 1, Target = DevicePrintTarget.FrontKitchen,
                DeviceId = "front-device", Status = DevicePrintStatus.Printed,
            },
        ];

        var acks = TelemetryPayloads.PrintAcks(
            order, true, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Sent, new PrinterConfiguration { FrontKitchenPrinterName = "front" },
            DateTime.UtcNow, "front-device");

        Assert.Empty(acks);

        order.RoutingStates[0].Status = DevicePrintStatus.Sent;
        Assert.Empty(TelemetryPayloads.PrintAcks(
            order, true, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Sent, new PrinterConfiguration { FrontKitchenPrinterName = "front" },
            DateTime.UtcNow, "front-device"));

        order.RoutingStates[0].JobId = Guid.Empty;
        Assert.Empty(TelemetryPayloads.PrintAcks(
            order, true, KitchenPrintOutcome.Sent, KitchenPrintOutcome.Sent,
            KitchenPrintOutcome.Sent, new PrinterConfiguration(), DateTime.UtcNow, "front-device"));
    }
}
