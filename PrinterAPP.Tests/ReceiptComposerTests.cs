using System.Text;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// The composed content of the per-item receipt block, pinned as text without a printer. These
/// cover the partner complaint: selected ingredients (an explicitly chosen sauce or topping is a
/// quantity-one, not-removed row) and sauces (side-item children) were filtered off the printed
/// paper; both surfaces must now print every row the backend froze at checkout, in the label
/// language the venue picked.
/// </summary>
public class ReceiptComposerTests
{
    private static readonly PrintLabels En = PrintLabelCatalog.For("en");
    private static readonly PrintLabels De = PrintLabelCatalog.For("de");

    private static string ComposeCashier(OrderItem item, PrintLabels? labels = null, string? currency = null)
    {
        var sb = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(sb, item, depth: 0, spacing: 32, labels ?? En, currency: currency);
        return sb.ToString();
    }

    private static string ComposeKitchen(OrderItem item, PrintLabels? labels = null)
    {
        var sb = new StringBuilder();
        ReceiptComposer.AppendKitchenItemLines(sb, item, depth: 0, labels ?? En);
        return sb.ToString();
    }

    private static OrderItem CustomizedKebab() => new()
    {
        ProductName = "Adana Kebab",
        Quantity = 1,
        IngredientCustomizations =
        [
            new IngredientCustomization { IngredientName = "Bread", Quantity = 1, IsRemoved = false },
            new IngredientCustomization { IngredientName = "Onion", Quantity = 0, IsRemoved = true },
            new IngredientCustomization { IngredientName = "Hot Sauce", Quantity = 3, IsRemoved = false },
            new IngredientCustomization { IngredientName = "Garlic", Quantity = 1, IsRemoved = false },
        ],
    };

    [Fact]
    public void Cashier_prints_selected_removed_and_extra_ingredients()
    {
        var ticket = ComposeCashier(CustomizedKebab());

        Assert.Contains("+ Bread", ticket);          // selected at quantity one — the complaint
        Assert.Contains("- NO Onion", ticket);       // removed
        Assert.Contains("+ EXTRA Hot Sauce x3", ticket); // quantity above one
        Assert.Contains("+ Garlic", ticket);
    }

    [Fact]
    public void Kitchen_prints_selected_removed_and_extra_ingredients()
    {
        var ticket = ComposeKitchen(CustomizedKebab());

        Assert.Contains("+ Bread", ticket);
        Assert.Contains("- NO Onion", ticket);
        Assert.Contains("+ EXTRA Hot Sauce x3", ticket);
        Assert.Contains("+ Garlic", ticket);
    }

    [Fact]
    public void Quantity_one_paid_add_on_prints_extra_on_both_receipt_surfaces()
    {
        var item = new OrderItem
        {
            ProductName = "Tacos 1 Viande",
            Quantity = 1,
            IngredientCustomizations =
            [
                new IngredientCustomization
                {
                    IngredientName = "Cheddar",
                    Quantity = 1,
                    IsAddOn = true,
                },
            ],
        };

        Assert.Contains("+ EXTRA Cheddar x1", ComposeCashier(item));
        Assert.Contains("+ EXTRA Cheddar x1", ComposeKitchen(item));
    }

    [Fact]
    public void Zero_quantity_add_on_does_not_print_as_extra_on_both_receipt_surfaces()
    {
        var item = new OrderItem
        {
            ProductName = "Tacos 1 Viande",
            Quantity = 1,
            IngredientCustomizations =
            [
                new IngredientCustomization
                {
                    IngredientName = "Cheddar",
                    Quantity = 0,
                    IsAddOn = true,
                },
            ],
        };

        var cashier = ComposeCashier(item);
        var kitchen = ComposeKitchen(item);

        Assert.DoesNotContain("Cheddar", cashier);
        Assert.DoesNotContain("Cheddar", kitchen);
        Assert.DoesNotContain("+ EXTRA Cheddar", cashier);
        Assert.DoesNotContain("+ EXTRA Cheddar", kitchen);
    }

    [Fact]
    public void Removed_add_on_keeps_removal_precedence()
    {
        var item = new OrderItem
        {
            ProductName = "Tacos 1 Viande",
            Quantity = 1,
            IngredientCustomizations =
            [
                new IngredientCustomization
                {
                    IngredientName = "Onion",
                    Quantity = 1,
                    IsAddOn = true,
                    IsRemoved = true,
                },
            ],
        };

        var cashier = ComposeCashier(item);
        var kitchen = ComposeKitchen(item);

        Assert.Contains("- NO Onion", cashier);
        Assert.Contains("- NO Onion", kitchen);
        Assert.DoesNotContain("EXTRA Onion", cashier);
        Assert.DoesNotContain("EXTRA Onion", kitchen);
    }

    [Fact]
    public void Kitchen_keeps_wide_item_line_and_tall_customization_commands()
    {
        var ticket = ComposeKitchen(CustomizedKebab());
        var wide = EscPosCommands.SizeWide;
        var tall = EscPosCommands.SizeTall;

        Assert.True(ticket.Contains(wide), "item line lost its wide-size command");
        Assert.True(ticket.Contains(tall), "customization lines lost their tall-size command");
    }

    [Fact]
    public void Type_and_table_line_sanitizes_control_characters_in_table_label()
    {
        var order = new Order
        {
            Type = "DineIn",
            TableLabel = "T\u001b@\r\nQA",
        };
        var builder = new StringBuilder();

        ReceiptComposer.AppendTypeAndTableLine(builder, order, En);

        Assert.Contains("Table T @ QA", builder.ToString());
        Assert.DoesNotContain("T\u001b@\r\nQA", builder.ToString());
    }

    [Fact]
    public void Cashier_price_appears_once_on_the_parent_line_only()
    {
        var item = new OrderItem
        {
            ProductName = "Menu Deal",
            Quantity = 2,
            ItemTotal = 31.90m,
            SideItems =
            [
                new OrderItem { ProductName = "Fries", Quantity = 2 },
                new OrderItem { ProductName = "Ayran", Quantity = 1 },
            ],
        };

        var ticket = ComposeCashier(item);

        Assert.Contains("2x Menu Deal", ticket);
        Assert.Contains("+ 2x Fries", ticket);
        Assert.Contains("+ 1x Ayran", ticket);
    }

    [Fact]
    public void Cashier_price_uses_the_order_currency_when_declared()
    {
        var item = new OrderItem { ProductName = "Menu Deal", Quantity = 2, ItemTotal = 31.90m };

        Assert.Contains("EUR 31.90", ComposeCashier(item, currency: "EUR"));
        Assert.Contains("CHF 31.90", ComposeCashier(item, currency: "CHF"));
    }

    [Fact]
    public void Cashier_price_stays_bare_when_no_currency_is_known()
    {
        // C18: an unknown currency must never be invented into CHF — the bare amount prints.
        var ticket = ComposeCashier(new OrderItem { ProductName = "Menu Deal", Quantity = 2, ItemTotal = 31.90m });

        Assert.Contains("31.90", ticket);
        Assert.DoesNotContain("CHF", ticket);
        Assert.DoesNotContain("EUR", ticket);
    }

    [Fact]
    public void Cashier_prints_nested_components_with_their_own_customizations()
    {
        var item = new OrderItem
        {
            ProductName = "Menu Deal",
            Quantity = 1,
            ItemTotal = 18.00m,
            SideItems =
            [
                new OrderItem
                {
                    ProductName = "Pizza",
                    Quantity = 1,
                    IngredientCustomizations = [new IngredientCustomization { IngredientName = "Mushrooms", Quantity = 1, IsRemoved = false }],
                },
            ],
        };

        var ticket = ComposeCashier(item);

        Assert.Contains("+ 1x Pizza", ticket);
        Assert.Contains("+ Mushrooms", ticket);
    }

    /// <summary>
    /// #318 semantics, printer side: a true SideItem is stored PER UNIT of its parent and must
    /// scale; a bundle child is already line-absolute and must not; an unclassifiable row (Kind
    /// null) prints exactly as stored. The backend measured a stored 6 rendering as 18 when this
    /// distinction was lost.
    /// </summary>
    [Fact]
    public void Child_quantities_follow_the_kind_semantics()
    {
        var item = new OrderItem
        {
            ProductName = "Order",
            Quantity = 3,
            SideItems =
            [
                new OrderItem { ProductName = "Cola", Quantity = 2, Kind = "SideItem" },
                new OrderItem { ProductName = "Fries", Quantity = 2, Kind = "BundleChild" },
                new OrderItem { ProductName = "Mystery", Quantity = 2, Kind = null },
            ],
        };

        var ticket = ComposeCashier(item);

        Assert.Contains("+ 6x Cola", ticket);     // SideItem: 2 per unit x 3 units
        Assert.Contains("+ 2x Fries", ticket);    // BundleChild: already line-absolute
        Assert.Contains("+ 2x Mystery", ticket);  // unknown: never invent a multiplier
        Assert.DoesNotContain("+ 4x Fries", ticket);
    }

    [Fact]
    public void Kitchen_scales_side_items_the_same_way()
    {
        var item = new OrderItem
        {
            ProductName = "Combo",
            Quantity = 2,
            SideItems = [new OrderItem { ProductName = "Ayran", Quantity = 1, Kind = "SideItem" }],
        };

        var kitchen = ComposeKitchen(item);
        Assert.Contains("+ 2x Ayran", kitchen);

        var cashier = ComposeCashier(item);
        Assert.Contains("+ 2x Ayran", cashier);
    }

    [Fact]
    public void Product_customization_option_prints_recursively_on_both_surfaces()
    {
        var item = new OrderItem
        {
            ProductName = "Menu Tacos",
            Quantity = 2,
            SideItems =
            [
                new OrderItem
                {
                    ProductName = "Tacos 1 viande",
                    Quantity = 2,
                    Kind = "BundleChild",
                    SideItems =
                    [
                        new OrderItem
                        {
                            ProductName = "Extra viande",
                            Quantity = 2,
                            Kind = "CustomizationOption"
                        }
                    ]
                }
            ]
        };

        foreach (var ticket in new[] { ComposeKitchen(item), ComposeCashier(item) })
        {
            Assert.Contains("+ 2x Tacos 1 viande", ticket);
            Assert.Contains("+ 2x Extra viande", ticket);
            Assert.DoesNotContain("+ 4x Extra viande", ticket);
        }
    }

    [Fact]
    public void Special_instruction_prints_with_a_label_on_both_surfaces()
    {
        var item = new OrderItem
        {
            ProductName = "Lahmacun",
            Quantity = 1,
            SpecialInstructions = "extra crispy",
        };

        Assert.Contains("NOTE: extra crispy", ComposeKitchen(item));
        Assert.Contains("NOTE: extra crispy", ComposeCashier(item));
    }

    [Fact]
    public void Context_only_line_stays_parenthesised_on_the_kitchen_ticket()
    {
        var item = new OrderItem
        {
            ProductName = "Menu Deal",
            Quantity = 1,
            IsContextOnly = true,
            SideItems = [new OrderItem { ProductName = "Fries", Quantity = 2 }],
        };

        var ticket = ComposeKitchen(item);

        Assert.Contains("(1x Menu Deal)", ticket);
        Assert.Contains("+ 2x Fries", ticket);
    }

    [Fact]
    public void Labels_follow_the_selected_language()
    {
        var item = new OrderItem
        {
            ProductName = "Lahmacun",
            Quantity = 1,
            IngredientCustomizations = [new IngredientCustomization { IngredientName = "Zwiebeln", Quantity = 0, IsRemoved = true }],
            SpecialInstructions = "scharf",
        };

        var ticket = ComposeKitchen(item, De);

        Assert.Contains("- OHNE Zwiebeln", ticket);
        Assert.Contains("NOTIZ: scharf", ticket);

        var cashier = ComposeCashier(item, De);
        Assert.Contains("- OHNE Zwiebeln", cashier);
        Assert.Contains("NOTIZ: scharf", cashier);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
