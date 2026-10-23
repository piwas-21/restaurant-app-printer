using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class ReceiptLocalizationAndNotesTests
{
    [Theory]
    [InlineData("Takeaway", "À emporter")]
    [InlineData("TakeAway", "À emporter")]
    [InlineData("DineIn", "Sur place")]
    [InlineData("Delivery", "Livraison")]
    public void French_order_types_accept_current_and_legacy_wire_values(string wire, string expected)
    {
        var output = new StringBuilder();
        ReceiptComposer.AppendTypeAndTableLine(output, new Order { Type = wire }, PrintLabelCatalog.French);
        Assert.Contains($"Type: {expected}", output.ToString());
    }

    [Theory]
    [InlineData("Cash", "Espèces")]
    [InlineData("1", "Espèces")]
    [InlineData("CreditCard", "CARTE AU RESTAURANT")]
    [InlineData("DebitCard", "Carte de débit")]
    [InlineData("OnlinePayment", "Paiement en ligne")]
    [InlineData("MobilePayment", "Paiement mobile")]
    [InlineData("BankTransfer", "Virement bancaire")]
    public void French_tender_methods_are_localized(string wire, string expected) =>
        Assert.Equal(expected, PrintLabelCatalog.French.PaymentMethodLabel(wire));

    [Theory]
    [InlineData("Pending", "En attente")]
    [InlineData("1", "En attente")]
    [InlineData("Processing", "En cours")]
    [InlineData("Failed", "Échec")]
    [InlineData("Completed", "PAYÉ")]
    [InlineData("Refunded", "REMBOURSÉ")]
    [InlineData("PartiallyPaid", "PARTIELLEMENT PAYÉ")]
    public void French_tender_statuses_are_localized(string wire, string expected) =>
        Assert.Equal(expected, PrintLabelCatalog.French.PaymentStatusLabel(wire));

    [Fact]
    public void Unknown_wire_values_remain_visible()
    {
        Assert.Equal("Voucher", PrintLabelCatalog.French.PaymentMethodLabel("Voucher"));
        Assert.Equal("Review", PrintLabelCatalog.French.PaymentStatusLabel("Review"));
        Assert.Equal("Pickup", PrintLabelCatalog.French.OrderType("Pickup"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Item_and_nested_bread_instructions_print_even_when_ingredients_are_disabled(bool ingredients)
    {
        var item = new OrderItem { ProductName = "Assiette Kebab", Quantity = 1, SpecialInstructions = "Pain",
            SideItems = [new() { ProductName = "Assiette", Quantity = 1, SpecialInstructions = "Pain à part" }] };
        var output = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(output, item, 0, 48, PrintLabelCatalog.French,
            config: new PrinterConfiguration { PrintIngredientsOnCashierReceipt = ingredients });
        Assert.Contains("REMARQUE: Pain", output.ToString());
        Assert.Contains("REMARQUE: Pain à part", output.ToString());
        var kitchen = new StringBuilder();
        ReceiptComposer.AppendKitchenItemLines(kitchen, item, 0, PrintLabelCatalog.French);
        Assert.Contains("REMARQUE: Pain", kitchen.ToString());
        Assert.Contains("REMARQUE: Pain à part", kitchen.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bread_note_is_suppressed_only_if_the_duplicate_variation_is_actually_printed(bool variations)
    {
        var output = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(output,
            new OrderItem { ProductName = "Assiette", Quantity = 1, VariationName = "Pain", SpecialInstructions = "Pain" },
            0, 48, PrintLabelCatalog.French,
            config: new PrinterConfiguration { PrintVariationsOnCashierReceipt = variations });
        Assert.Equal(variations, output.ToString().Contains("(Pain)", StringComparison.Ordinal));
        Assert.Equal(!variations, output.ToString().Contains("REMARQUE: Pain", StringComparison.Ordinal));
    }
    [Fact]
    public void Named_dish_preserves_a_bread_note_when_its_variation_is_not_in_the_heading()
    {
        var item = new OrderItem { ProductName = "Menu", Quantity = 1,
            SideItems = [new() { ProductName = "Plate", CompositionRole = CompositionRole.Dish,
                PresentationLabel = "Assiette", Quantity = 1, VariationName = "Pain", SpecialInstructions = "Pain" }] };
        var output = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(output, item, 0, 48, PrintLabelCatalog.French,
            config: new PrinterConfiguration { PrintVariationsOnCashierReceipt = true });
        Assert.Contains("REMARQUE: Pain", output.ToString());
    }

    [Fact]
    public void Hidden_ingredient_subtrees_with_instructions_keep_their_preparation_context()
    {
        var item = new OrderItem { ProductName = "Menu", Quantity = 1,
            SideItems = [new() { ProductName = "Sauce", CompositionRole = CompositionRole.Sauce,
                Quantity = 1, SideItems = [new() { ProductName = "Bread", Quantity = 1, SpecialInstructions = "À part" }] }] };
        var output = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(output, item, 0, 48, PrintLabelCatalog.French,
            config: new PrinterConfiguration { PrintIngredientsOnCashierReceipt = false });
        Assert.Contains("Sauce", output.ToString());
        Assert.Contains("REMARQUE: À part", output.ToString());
    }

}
