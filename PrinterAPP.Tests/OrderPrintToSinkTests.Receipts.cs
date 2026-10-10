using Xunit;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    [Fact]
    public async Task Cashier_receipt_reports_unpaid_order_and_due_without_counting_pending_tender()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.RemainingAmount = order.Total;
        order.Payments = [new() { PaymentMethod = "CreditCard", Amount = order.Total, Status = "Pending" }];

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token);

        Assert.True(ok);
        Assert.Contains("PAYMENT STATUS: UNPAID", receipt);
        Assert.Contains("DUE: 12.50", receipt);
        Assert.DoesNotContain("PAID: 12.50", receipt);
        Assert.Contains("CARD AT RESTAURANT (Pending)", receipt);
        Assert.DoesNotContain("CARD AT RESTAURANT: 12.50", receipt);
    }

    [Fact]
    public async Task Cashier_receipt_separates_partial_paid_and_remaining_debt()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.TotalPaid = 5m;
        order.RemainingAmount = 7.50m;
        order.Payments = [new() { PaymentMethod = "Cash", Amount = 5m, Status = "Completed" }];

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token);

        Assert.True(ok);
        Assert.Contains("PAYMENT STATUS: PARTIALLY PAID", receipt);
        Assert.Contains("PAID: 5.00", receipt);
        Assert.Contains("DUE: 7.50", receipt);
        Assert.Contains("Cash: 5.00", receipt);
    }

    [Fact]
    public async Task Cashier_receipt_reports_paid_order_without_due()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.TotalPaid = order.Total;
        order.RemainingAmount = 0;
        order.IsFullyPaid = true;
        order.Payments = [new() { PaymentMethod = "Cash", Amount = order.Total, Status = "Completed" }];

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token);

        Assert.True(ok);
        Assert.Contains("PAYMENT STATUS: PAID", receipt);
        Assert.Contains("PAID: 12.50", receipt);
        Assert.DoesNotContain("DUE:", receipt);
    }

    [Fact]
    public async Task Cashier_receipt_honors_refunded_payment_status_over_paid_flags()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.PaymentStatus = "Refunded";
        order.IsFullyPaid = true;
        order.TotalPaid = 0;
        order.RemainingAmount = order.Total;

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token);

        Assert.True(ok);
        Assert.Contains("PAYMENT STATUS: REFUNDED", receipt);
        Assert.Contains("DUE: 12.50", receipt);
        Assert.DoesNotContain("PAID: 12.50", receipt);
    }

    [Fact]
    public async Task Cashier_receipt_reports_overpaid_status_and_credit_without_due()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.PaymentStatus = "Overpaid";
        order.IsFullyPaid = true;
        order.TotalPaid = 15m;
        order.RemainingAmount = -2.50m;

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token);

        Assert.True(ok);
        Assert.Contains("PAYMENT STATUS: OVERPAID", receipt);
        Assert.Contains("PAID: 15.00", receipt);
        Assert.Contains("CREDIT: 2.50", receipt);
        Assert.DoesNotContain("DUE:", receipt);
    }
}
