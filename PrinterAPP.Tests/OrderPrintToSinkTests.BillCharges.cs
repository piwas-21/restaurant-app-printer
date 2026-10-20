using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cashier_bill_prints_signed_nonzero_adjustments_and_effective_total(bool hasPayableTotal)
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
            Total = 20m, SubTotal = 20m, PayableTotal = hasPayableTotal ? 19.50m : null,
            BillChargeAmount = -0.50m, RemainingAmount = 19.50m,
            OrderDate = DateTime.Now,
            BillCharges = [new() { Key = "service_fee", Label = "Service fee", Amount = 5m, Currency = "CHF" },
                new() { Key = "discount", Label = "Fixed discount", Amount = -2m, Currency = "CHF" },
                new() { Key = "adjustment", Label = "Custom reduction", Amount = -3.50m, Currency = "CHF" },
                new() { Key = "delivery_fee", Label = "Optional delivery fee", Amount = 0m, Currency = "CHF" }],
            Items = [new() { ProductName = "Soup", Quantity = 1, UnitPrice = 20m, ItemTotal = 20m }]
        };
        Assert.True(await service.PrintOrderAsync(order, PrinterType.Cashier, isManualPrint: true, cts.Token));
        var receipt = await sink.ReadTicketAsync(cts.Token);
        Assert.Contains("Service fee: CHF 5.00", receipt);
        Assert.Contains("Fixed discount: CHF -2.00", receipt);
        Assert.Contains("Custom reduction: CHF -3.50", receipt);
        Assert.Contains("TOTAL: CHF 19.50", receipt);
        Assert.DoesNotContain("Optional delivery fee", receipt);
    }
}
