using Xunit;

using PrinterAPP.Models;
using PrinterAPP.Services;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    private static readonly string CustomerEmailFixture = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "receipt-customer-email.txt")).Trim();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cashier_ingredient_switch_controls_bytes_sent_to_printer(bool enabled)
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.Items[0].IngredientCustomizations = [new() { IngredientName = "Onion", IsRemoved = true },
            new() { IngredientName = "Garlic sauce", Quantity = 1, IsAddOn = true }];
        order.Items[0].SpecialInstructions = "Kitchen only";
        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token,
            config => config.PrintIngredientsOnCashierReceipt = enabled);
        Assert.True(ok);
        Assert.Equal(enabled, receipt.Contains("Onion", StringComparison.Ordinal));
        Assert.Equal(enabled, receipt.Contains("Garlic sauce", StringComparison.Ordinal));
        Assert.DoesNotContain("Kitchen only", receipt);
        Assert.Contains("12.50", receipt);
    }

    [Fact]
    public async Task Cashier_receipt_sends_daily_number_and_enabled_contact_and_preparation_details_to_sink()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.OrderNumber = "2026101500007";
        order.DailyOrderNumber = 7;
        order.BusinessDate = new DateOnly(2026, 10, 15);
        order.BusinessTimeZoneId = "Europe/Zurich";
        order.CustomerPhone = "+41 79 555 01 02";
        order.CustomerEmail = CustomerEmailFixture;
        order.EffectivePreparationMinutes = 25;
        order.ExpectedReadyAt = new DateTime(2026, 10, 15, 12, 45, 0, DateTimeKind.Utc);

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token,
            config => config.CashierPaperWidth = 58);

        Assert.True(ok);
        Assert.Contains("#7", receipt);
        Assert.Contains("2026101500007", receipt);
        Assert.Contains("Tel: +41 79 555 01 02", receipt);
        Assert.Contains($"Email: {CustomerEmailFixture}", receipt);
        Assert.Contains("Prep: 25 min", receipt);
        Assert.Contains("Ready at: 15/10 14:45", receipt);

        var numberIndex = receipt.IndexOf("#7", StringComparison.Ordinal);
        Assert.True(numberIndex > 0);
        Assert.True(receipt.LastIndexOf(EscPosCommands.SizeDouble, numberIndex, StringComparison.Ordinal) >= 0);
        Assert.True(receipt.LastIndexOf(EscPosCommands.BoldOn, numberIndex, StringComparison.Ordinal) >= 0);
    }

    [Theory]
    [InlineData(58, 32)]
    [InlineData(80, 48)]
    public async Task Kitchen_ticket_prints_daily_number_and_separates_root_product_groups_at_each_paper_width(
        int paperWidth, int separatorWidth)
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.OrderNumber = "2026101500007";
        order.DailyOrderNumber = 7;
        order.Items =
        [
            new OrderItem
            {
                Id = "menu-root",
                ProductName = "Menu Deal",
                Quantity = 1,
                CompositionRole = CompositionRole.Menu,
                SideItems =
                [
                    new OrderItem
                    {
                        Id = "dish",
                        ProductName = "Taco",
                        Quantity = 1,
                        CompositionRole = CompositionRole.Dish,
                        PresentationLabel = "Taco",
                        SideItems =
                        [
                            new OrderItem
                            {
                                Id = "choice",
                                ProductName = "Beef",
                                Quantity = 1,
                                CompositionRole = CompositionRole.RequiredChoice,
                                Kind = "BundleChild",
                            },
                        ],
                    },
                ],
            },
            new OrderItem
            {
                ProductName = "Cola",
                Quantity = 1,
                CompositionRole = CompositionRole.Drink,
            },
        ];

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token,
            config => config.KitchenPaperWidth = paperWidth,
            printerType: PrinterType.Kitchen);

        Assert.True(ok);
        Assert.Contains("#7", receipt);
        Assert.Contains("2026101500007", receipt);
        Assert.Contains(EscPosCommands.SizeDouble, receipt);
        Assert.Contains(EscPosCommands.BoldOn, receipt);
        Assert.Contains(new string('-', separatorWidth), receipt);
        var componentIndex = receipt.IndexOf("Beef", StringComparison.Ordinal);
        var secondProductIndex = receipt.IndexOf("Cola", StringComparison.Ordinal);
        var separatorIndex = receipt.IndexOf(new string('-', separatorWidth), componentIndex, StringComparison.Ordinal);
        Assert.True(componentIndex >= 0 && separatorIndex > componentIndex && separatorIndex < secondProductIndex, receipt);
    }

    [Fact]
    public async Task Cashier_receipt_preferences_can_opt_out_of_each_optional_field()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var order = MinimalOrder();
        order.CustomerPhone = "+41 79 555 01 02";
        order.CustomerEmail = CustomerEmailFixture;
        order.EffectivePreparationMinutes = 25;
        order.ExpectedReadyAt = new DateTime(2026, 10, 15, 12, 45, 0, DateTimeKind.Utc);
        order.BusinessTimeZoneId = "Europe/Zurich";

        var (ok, receipt) = await PrintToSinkAsync(order, sink, cancellation.Token, config =>
        {
            config.PrintCustomerPhoneOnCashierReceipt = false;
            config.PrintCustomerEmailOnCashierReceipt = false;
            config.PrintPreparationMinutesOnCashierReceipt = false;
            config.PrintExpectedReadyAtOnCashierReceipt = false;
        });

        Assert.True(ok);
        Assert.DoesNotContain("Tel:", receipt);
        Assert.DoesNotContain("Email:", receipt);
        Assert.DoesNotContain("Prep:", receipt);
        Assert.DoesNotContain("Ready at:", receipt);
    }

    [Fact]
    public async Task Cashier_receipt_omits_missing_optional_details_cleanly()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (ok, receipt) = await PrintToSinkAsync(MinimalOrder(), sink, cancellation.Token);

        Assert.True(ok);
        Assert.DoesNotContain("Tel:", receipt);
        Assert.DoesNotContain("Email:", receipt);
        Assert.DoesNotContain("Prep:", receipt);
        Assert.DoesNotContain("Ready at:", receipt);
    }

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
