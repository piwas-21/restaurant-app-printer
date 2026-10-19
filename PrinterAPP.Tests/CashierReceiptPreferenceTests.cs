using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class CashierReceiptPreferenceTests
{
    [Fact]
    public void Mixed_refunds_show_paid_and_refunded_amount_context()
    {
        var order = new Order { PaymentStatus = "PartiallyRefunded", TotalPaid = 20, IsTableBill = true,
            CashierBillRefundedAmount = 10 };
        var labels = PrintLabelCatalog.For("fr");
        var state = CashierPaymentPresentation.State(order, labels);
        Assert.Contains(labels.Paid, state);
        Assert.Contains(labels.Refunded, state);
        Assert.NotEqual(labels.Refunded, state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Variation_preference_applies_to_root_and_nested_lines_without_ingredients(bool variations)
    {
        var item = new OrderItem { ProductName = "Kofte Grill", VariationName = "Frites", Quantity = 1,
            ItemTotal = 25.90m, SideItems = [new() { ProductName = "Tea", VariationName = "Medium", Quantity = 1,
                IngredientCustomizations = [new() { IngredientName = "Sugar", IsRemoved = true }] }] };
        var output = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(output, item, 0, 48, PrintLabelCatalog.For("fr"),
            currency: "CHF", includeVariations: variations);
        var bill = output.ToString();
        Assert.Equal(variations, bill.Contains("(Frites)", StringComparison.Ordinal));
        Assert.Equal(variations, bill.Contains("(Medium)", StringComparison.Ordinal));
        Assert.DoesNotContain("Sugar", bill);
        Assert.Contains("CHF 25.90", bill);
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void Automatic_bills_honor_quiet_hours_while_manual_claims_remain_available(int hour, bool expected)
    {
        var config = new PrinterConfiguration { EnableTimeRestriction = true,
            RestrictStartTime = TimeSpan.FromHours(12), RestrictEndTime = TimeSpan.FromHours(13) };
        Assert.Equal(expected, CashierPrintJobClient.AllowsAutomatic(config, TimeSpan.FromHours(hour)));
    }

    [Fact]
    public void Genuine_prefixed_and_pipe_comments_are_preserved()
    {
        var item = new OrderItem { VariationName = "Medium", SpecialInstructions = "Please serve very hot | No: boiling water",
            IngredientCustomizations = [new() { IngredientName = "Sugar", Quantity = 1, IsAddOn = true }] };
        Assert.Equal(item.SpecialInstructions, ReceiptItemDisplay.DisplaySpecialInstructions(item));
    }
}
