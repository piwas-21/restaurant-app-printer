using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    [Fact]
    public async Task Cashier_bill_prints_only_positive_charges_and_effective_total()
    {
        using var sink = new Sink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var paths = new TempPathProvider();
        var service = new OrderPrintService(new MarketplaceReceiptComposer(),
            new StubPrinterService(new PrinterConfiguration
            {
                CashierPrinterName = sink.PrinterName, CashierAutoPrint = true, CashierPrintCopies = 1
            }), new CapturingRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        var order = new Order
        {
            OrderNumber = "FEE-TEST", Type = "Takeaway", Status = "Confirmed", Currency = "CHF",
            Total = 20m, SubTotal = 20m, PayableTotal = 25m, BillChargeAmount = 5m, RemainingAmount = 25m,
            OrderDate = DateTime.Now,
            BillCharges = [new() { Key = "service_fee", Label = "Service fee", Amount = 5m, Currency = "CHF" },
                new() { Key = "delivery_fee", Label = "Optional delivery fee", Amount = 0m, Currency = "CHF" }],
            Items = [new() { ProductName = "Soup", Quantity = 1, UnitPrice = 20m, ItemTotal = 20m }]
        };
        Assert.True(await service.PrintOrderAsync(order, PrinterType.Cashier, isManualPrint: true, cts.Token));
        var receipt = await sink.ReadTicketAsync(cts.Token);
        Assert.Contains("Service fee: CHF 5.00", receipt);
        Assert.Contains("CHF 25.00", receipt);
        Assert.DoesNotContain("Optional delivery fee", receipt);
    }
}
