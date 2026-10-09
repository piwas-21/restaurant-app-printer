using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    [Theory]
    [InlineData(300, 0, "Additional tip: CHF 3.00", "PAID: CHF 23.00", "Cash: CHF 23.00")]
    [InlineData(200, 100, "Additional tip: CHF 2.00", "PAID: CHF 22.00", "Cash: CHF 22.00")]
    public async Task Staff_gratuity_prints_separately_from_food_debt_and_matches_net_cash(
        long netTip, long refundedTip, string tipLine, string paidLine, string paymentLine)
    {
        var order = LocalCashOrder();
        order.PaymentTipMinor = netTip;
        order.Payments[0].TipMinor = 300;
        order.Payments[0].RefundedTipMinor = refundedTip;
        var ticket = await PrintTipReceiptAsync(order);
        Assert.Contains("TOTAL: CHF 20.00", ticket);
        Assert.Contains(tipLine, ticket);
        Assert.Contains(paidLine, ticket);
        Assert.Contains(paymentLine, ticket);
        Assert.DoesNotContain("DUE:", ticket);
        Assert.Equal(20m, order.Total);
        Assert.Equal(20m, order.TotalPaid);
    }

    [Fact]
    public async Task Old_payload_without_staff_tip_keeps_the_original_total_and_paid_amount()
    {
        var ticket = await PrintTipReceiptAsync(LocalCashOrder());
        Assert.Contains("TOTAL: CHF 20.00", ticket);
        Assert.Contains("PAID: CHF 20.00", ticket);
        Assert.DoesNotContain("Additional tip:", ticket);
    }

    private static Order LocalCashOrder() => new()
    {
        Id = Guid.NewGuid().ToString(), OrderNumber = "TIP-SINK-01", Type = "TakeAway",
        Status = "Confirmed", Currency = "CHF", OrderDate = DateTime.UtcNow,
        SubTotal = 20m, Total = 20m, TotalPaid = 20m,
        Items = [new() { ProductName = "Adana Grill", Quantity = 1, UnitPrice = 20m, ItemTotal = 20m }],
        Payments = [new() { PaymentMethod = "Cash", Amount = 20m, Status = "Completed" }],
    };

    private static async Task<string> PrintTipReceiptAsync(Order order)
    {
        using var sink = new Sink();
        using var paths = new TempPathProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = new OrderPrintService(new MarketplaceReceiptComposer(),
            new StubPrinterService(new PrinterConfiguration
            {
                CashierPrinterName = sink.PrinterName, CashierAutoPrint = true, CashierPrintCopies = 1
            }), new CapturingRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.True(await service.PrintOrderAsync(order, PrinterType.Cashier, isManualPrint: true, cts.Token));
        return await sink.ReadTicketAsync(cts.Token);
    }
}
