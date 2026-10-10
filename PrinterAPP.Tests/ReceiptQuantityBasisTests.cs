using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class ReceiptQuantityBasisTests
{
    [Fact]
    public void Line_total_components_under_repeated_dishes_are_not_divided_or_scaled()
    {
        var menu = new OrderItem
        {
            Id = "10000000-0000-0000-0000-000000000001",
            ProductName = "Lunch Menu",
            Quantity = 2,
            UnitPrice = 10,
            ItemTotal = 20,
            QuantityBasis = QuantityBasis.LineTotal,
            CompositionRole = CompositionRole.Menu,
            SideItems =
            [
                new OrderItem
                {
                    Id = "20000000-0000-0000-0000-000000000001",
                    ProductName = "Taco selection",
                    Quantity = 4,
                    QuantityBasis = QuantityBasis.LineTotal,
                    CompositionRole = CompositionRole.Dish,
                    PresentationLabel = "Taco",
                    SideItems =
                    [
                        new OrderItem
                        {
                            Id = "30000000-0000-0000-0000-000000000001",
                            ProductName = "Kebab",
                            Quantity = 4,
                            QuantityBasis = QuantityBasis.LineTotal,
                            CompositionRole = CompositionRole.RequiredChoice,
                            ParentComponentOrderItemId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                            SectionId = "40000000-0000-0000-0000-000000000001",
                        },
                        new OrderItem
                        {
                            Id = "30000000-0000-0000-0000-000000000002",
                            ProductName = "Fries",
                            Quantity = 8,
                            QuantityBasis = QuantityBasis.LineTotal,
                            CompositionRole = CompositionRole.Side,
                            ParentComponentOrderItemId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                        },
                    ],
                },
            ],
        };

        var builder = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(
            builder, menu, 0, 32, PrintLabelCatalog.English, currency: "EUR");
        var receipt = builder.ToString();

        Assert.Contains("4x Taco", receipt);
        Assert.Contains("4x Kebab", receipt);
        Assert.Contains("8x Fries", receipt);
        Assert.DoesNotContain("8x Kebab", receipt);
        Assert.DoesNotContain("16x Fries", receipt);
        Assert.DoesNotContain("For each Taco", receipt);
    }

    [Fact]
    public void Per_parent_side_uses_the_immediate_choice_owner_on_kitchen_and_cashier_receipts()
    {
        var kebabId = Guid.Parse("30000000-0000-0000-0000-000000000011");
        var menu = new OrderItem
        {
            Id = "10000000-0000-0000-0000-000000000011",
            ProductName = "Lunch Menu",
            Quantity = 2,
            QuantityBasis = QuantityBasis.LineTotal,
            CompositionRole = CompositionRole.Menu,
            SideItems =
            [
                new OrderItem
                {
                    Id = "20000000-0000-0000-0000-000000000011",
                    ProductName = "Taco selection",
                    Quantity = 4,
                    QuantityBasis = QuantityBasis.LineTotal,
                    CompositionRole = CompositionRole.Dish,
                    PresentationLabel = "Taco",
                    SideItems =
                    [
                        new OrderItem
                        {
                            Id = kebabId.ToString(),
                            ProductName = "Kebab",
                            Quantity = 4,
                            QuantityBasis = QuantityBasis.LineTotal,
                            CompositionRole = CompositionRole.RequiredChoice,
                            ParentComponentOrderItemId = Guid.Parse("20000000-0000-0000-0000-000000000011"),
                            SectionId = "40000000-0000-0000-0000-000000000011",
                        },
                        new OrderItem
                        {
                            Id = "30000000-0000-0000-0000-000000000012",
                            ProductName = "Fries",
                            Quantity = 2,
                            QuantityBasis = QuantityBasis.PerParentUnit,
                            ConfigurationScope = ConfigurationScope.SharedAcrossParentUnits,
                            CompositionRole = CompositionRole.Side,
                            ParentComponentOrderItemId = kebabId,
                        },
                    ],
                },
            ],
        };

        var cashierBuilder = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(cashierBuilder, menu, 0, 32, PrintLabelCatalog.English);
        var cashier = cashierBuilder.ToString();

        var kitchenBuilder = new StringBuilder();
        ReceiptComposer.AppendKitchenItemLines(kitchenBuilder, menu, 0, PrintLabelCatalog.English);
        var kitchen = kitchenBuilder.ToString();

        Assert.Contains("2x Fries (each)", cashier);
        Assert.Contains("2x Fries (each)", kitchen);
        Assert.DoesNotContain("For each Taco", cashier);
        Assert.DoesNotContain("For each Taco", kitchen);
        Assert.DoesNotContain("4x Fries", cashier);
        Assert.DoesNotContain("4x Fries", kitchen);
    }

    [Fact]
    public void Per_parent_scope_marker_is_shown_only_when_the_immediate_owner_repeats()
    {
        var owner = new OrderItem
        {
            ProductName = "Dish",
            Quantity = 1,
            QuantityBasis = QuantityBasis.LineTotal,
            CompositionRole = CompositionRole.Dish,
            SideItems =
            [
                new OrderItem
                {
                    ProductName = "Fries",
                    Quantity = 2,
                    QuantityBasis = QuantityBasis.PerParentUnit,
                    ConfigurationScope = ConfigurationScope.SharedAcrossParentUnits,
                    CompositionRole = CompositionRole.Side,
                },
            ],
        };

        var singleBuilder = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(singleBuilder, owner, 0, 32, PrintLabelCatalog.English);
        Assert.Contains("2x Fries", singleBuilder.ToString());
        Assert.DoesNotContain("(each)", singleBuilder.ToString());

        owner.Quantity = 2;
        var repeatedBuilder = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(repeatedBuilder, owner, 0, 32, PrintLabelCatalog.English);
        Assert.Contains("2x Fries (each)", repeatedBuilder.ToString());
    }
}
