using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class BackendPrinterFeedContractTests
{
    private static readonly string FeedFixturePath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "backend-printer-feed-v2.golden.json");

    [Fact]
    public void Actual_backend_checkout_feed_parses_and_composes_quantities_without_rescaling_at_both_widths()
    {
        var parsed = OrderFeedParser.Parse(File.ReadAllText(FeedFixturePath));
        Assert.True(parsed.IsSuccess, parsed.FailureMessage);
        Assert.Equal(2, parsed.ProjectionVersion);
        Assert.Empty(parsed.Errors);
        Assert.Empty(parsed.UpdateErrors);
        Assert.Single(parsed.Updates);

        var order = Assert.Single(parsed.Orders);
        var roots = ReceiptCompositionProjection.Build(order.Items);
        Assert.Equal(3, roots.Count);
        Assert.Equal(new[] { "root-q2", "root-q3", "root-q1" },
            roots.Select(item => item.SpecialInstructions));

        foreach (var quantity in new[] { 1, 2, 3 })
        {
            var root = Assert.Single(roots, item => item.SpecialInstructions == $"root-q{quantity}");
            Assert.Equal(quantity, root.Quantity);
            Assert.Equal(QuantityBasis.LineTotal, root.QuantityBasis);
            Assert.Equal(CompositionRole.Menu, root.CompositionRole);

            var dish = Assert.Single(root.SideItems!, item => item.CompositionRole == CompositionRole.Dish);
            Assert.Equal(quantity, dish.Quantity);
            Assert.Equal(QuantityBasis.LineTotal, dish.QuantityBasis);
            Assert.Equal("Taco", dish.ProductName);

            var beef = Assert.Single(dish.SideItems!, item => item.ProductName == "Beef");
            Assert.Equal(quantity * 2, beef.Quantity);
            Assert.Equal(QuantityBasis.LineTotal, beef.QuantityBasis);
            Assert.Equal(CompositionRole.RequiredChoice, beef.CompositionRole);
            Assert.Equal(dish.Id, beef.ParentComponentOrderItemId?.ToString());
            Assert.Equal("Taco", beef.PresentationLabel);

            var steak = Assert.Single(dish.SideItems!, item => item.ProductName == "Steak");
            Assert.Equal(quantity, steak.Quantity);
            Assert.Equal(QuantityBasis.LineTotal, steak.QuantityBasis);
            Assert.Equal(CompositionRole.RequiredChoice, steak.CompositionRole);
            Assert.Equal(dish.Id, steak.ParentComponentOrderItemId?.ToString());

            var sides = dish.SideItems!.Where(item => item.CompositionRole == CompositionRole.Side).ToList();
            Assert.Equal(2, sides.Count);
            Assert.All(sides, side =>
            {
                Assert.Equal(QuantityBasis.PerParentUnit, side.QuantityBasis);
                Assert.Equal(ConfigurationScope.SharedAcrossParentUnits, side.ConfigurationScope);
            });
            Assert.Equal(1, Assert.Single(sides, side => side.ProductName == "Side One").Quantity);
            Assert.Equal(2, Assert.Single(sides, side => side.ProductName == "Side Two").Quantity);
            var drink = Assert.Single(dish.SideItems!, item => item.ProductName == "Side Three");
            Assert.Equal(CompositionRole.Drink, drink.CompositionRole);
            Assert.Equal(3, drink.Quantity);
            Assert.Equal(QuantityBasis.PerParentUnit, drink.QuantityBasis);
            Assert.Equal(ConfigurationScope.SharedAcrossParentUnits, drink.ConfigurationScope);

            var removed = Assert.Single(dish.IngredientCustomizations!, row => row.IngredientName == "Cheese");
            Assert.True(removed.IsRemoved);
            Assert.Equal(0, removed.Quantity);
            Assert.Equal(QuantityBasis.PerParentUnit, removed.QuantityBasis);
            var extra = Assert.Single(dish.IngredientCustomizations!, row => row.IngredientName == "Chili");
            Assert.True(extra.IsAddOn);
            Assert.Equal(2, extra.Quantity);
            Assert.Equal(QuantityBasis.PerParentUnit, extra.QuantityBasis);
            Assert.Equal(CompositionRole.Extra, extra.CompositionRole);
        }

        foreach (var widthMillimeters in new[] { 58, 80 })
        {
            var cashier = ComposeCashier(roots, widthMillimeters);
            var kitchen = ComposeKitchen(roots);
            foreach (var quantity in new[] { 1, 2, 3 })
            {
                Assert.Contains($"{quantity}x Menu Deal", cashier);
                Assert.Contains($"Total for {quantity} menus: Beef ×{quantity * 2}; Steak ×{quantity}", cashier);
                Assert.Contains($"For each Taco: Side One ×1", cashier);
                Assert.Contains($"For each Taco: Side Two ×2", cashier);
                Assert.Contains($"For each Taco: Side Three ×3", cashier);
            }

            Assert.Equal(3, Count(cashier, "Menu Deal"));
            Assert.Equal(3, Count(kitchen, "Menu Deal"));
            Assert.Contains("- NO Cheese", cashier);
            Assert.Contains("Chili x2", cashier);
            Assert.DoesNotContain("Taco selection", cashier);
            Assert.DoesNotContain("Beef ×12", cashier);
            Assert.DoesNotContain("Side Two ×4", cashier);
            Assert.DoesNotContain("Side Three ×9", cashier);
            Assert.DoesNotContain("EUR 0.00", cashier);

            Assert.True(kitchen.IndexOf("Total for 2 menus: Beef ×4; Steak ×2", StringComparison.Ordinal)
                < kitchen.IndexOf("Chili", StringComparison.Ordinal));
            Assert.True(kitchen.IndexOf("Chili", StringComparison.Ordinal)
                < kitchen.IndexOf("Side One", StringComparison.Ordinal));
            Assert.True(kitchen.IndexOf("Side Two", StringComparison.Ordinal)
                < kitchen.IndexOf("Side Three", StringComparison.Ordinal));
            Assert.True(cashier.IndexOf("Side Two", StringComparison.Ordinal)
                < cashier.IndexOf("Side Three", StringComparison.Ordinal));
        }
    }

    private static string ComposeCashier(IReadOnlyList<OrderItem> roots, int widthMillimeters)
    {
        var builder = new StringBuilder();
        var spacing = widthMillimeters == 58 ? 32 : 48;
        foreach (var root in roots)
            ReceiptComposer.AppendCashierItemLines(builder, root, 0, spacing, PrintLabelCatalog.English, currency: "EUR");
        return builder.ToString();
    }

    private static string ComposeKitchen(IEnumerable<OrderItem> roots)
    {
        var builder = new StringBuilder();
        foreach (var root in roots)
            ReceiptComposer.AppendKitchenItemLines(builder, root, 0, PrintLabelCatalog.English);
        return builder.ToString();
    }

    private static int Count(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
