using System.Text;
using System.Text.Json;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// The offer-family catalogue is a presentation change: once a guest chooses an offer, the
/// printer still receives the existing ordinary-product or menu-bundle order shape. These fixtures
/// pin the three important shapes from the redesign and prove that a variation selected inside a
/// menu remains visible on both receipt surfaces without a catalogue lookup.
/// </summary>
public sealed class OfferFamilyPrintContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly PrintLabels Labels = PrintLabelCatalog.For("en");

    [Fact]
    public void Ordinary_product_prints_its_frozen_name_and_price()
    {
        var order = DeserializeOrder("""
        {
          "orderNumber": "202609170001",
          "currency": "EUR",
          "items": [
            {
              "productId": "11111111-1111-1111-1111-111111111111",
              "productName": "Sandwich Kebab",
              "quantity": 1,
              "unitPrice": 9.00,
              "itemTotal": 9.00,
              "kitchenType": "FrontKitchen"
            }
          ]
        }
        """);

        var cashier = ComposeCashier(order.Items[0], order.Currency);
        var kitchen = ComposeKitchen(order.Items[0]);

        Assert.Contains("1x Sandwich Kebab", cashier);
        Assert.Contains("EUR 9.00", cashier);
        Assert.Contains("1x Sandwich Kebab", kitchen);
        Assert.DoesNotContain("Menu", cashier);
    }

    [Fact]
    public void Ordinary_menu_bundle_prints_the_menu_identity_and_nested_components()
    {
        var order = DeserializeOrder("""
        {
          "orderNumber": "202609170002",
          "items": [
            {
              "productId": "22222222-2222-2222-2222-222222222222",
              "menuID": "22222222-2222-2222-2222-222222222222",
              "productName": "Menu Sandwich Kebab",
              "quantity": 1,
              "unitPrice": 12.00,
              "itemTotal": 12.00,
              "kitchenType": "FrontKitchen",
              "sideItems": [
                {
                  "productId": "33333333-3333-3333-3333-333333333333",
                  "productName": "Fries",
                  "quantity": 1,
                  "kind": "BundleChild"
                },
                {
                  "productId": "44444444-4444-4444-4444-444444444444",
                  "productName": "Cola",
                  "quantity": 1,
                  "kind": "BundleChild"
                }
              ]
            }
          ]
        }
        """);

        var item = Assert.Single(order.Items);
        var cashier = ComposeCashier(item, order.Currency);
        var kitchen = ComposeKitchen(item);

        Assert.Equal("22222222-2222-2222-2222-222222222222", item.MenuID);
        Assert.Equal("Menu Sandwich Kebab", item.ProductName);
        Assert.Contains("1x Menu Sandwich Kebab", cashier);
        Assert.Contains("+ 1x Fries", cashier);
        Assert.Contains("+ 1x Cola", cashier);
        Assert.Contains("1x Menu Sandwich Kebab", kitchen);
        Assert.Contains("+ 1x Fries", kitchen);
        Assert.Contains("+ 1x Cola", kitchen);
    }

    [Fact]
    public void Variation_linked_menu_bundle_prints_the_child_variation_snapshot()
    {
        var variationId = "55555555-5555-5555-5555-555555555555";
        var order = DeserializeOrder("""
        {
          "orderNumber": "202609170003",
          "items": [
            {
              "productId": "66666666-6666-6666-6666-666666666666",
              "menuID": "66666666-6666-6666-6666-666666666666",
              "productName": "Menu Nuggets",
              "quantity": 1,
              "unitPrice": 13.00,
              "itemTotal": 13.00,
              "kitchenType": "FrontKitchen",
              "sideItems": [
                {
                  "productId": "77777777-7777-7777-7777-777777777777",
                  "productVariationId": "55555555-5555-5555-5555-555555555555",
                  "productName": "Nuggets",
                  "variationName": "12 pieces",
                  "quantity": 1,
                  "kind": "BundleChild",
                  "kitchenType": "FrontKitchen"
                },
                {
                  "productId": "88888888-8888-8888-8888-888888888888",
                  "productName": "Cola",
                  "quantity": 1,
                  "kind": "BundleChild"
                }
              ]
            }
          ]
        }
        """);

        var menu = Assert.Single(order.Items);
        var nuggets = Assert.Single(menu.SideItems!, item => item.ProductName == "Nuggets");
        var cashier = ComposeCashier(menu, order.Currency);
        var kitchen = ComposeKitchen(menu);

        Assert.Equal(variationId, nuggets.ProductVariationId);
        Assert.Equal("12 pieces", nuggets.VariationName);
        Assert.Contains("+ 1x Nuggets (12 pieces)", cashier);
        Assert.Contains("+ 1x Nuggets", kitchen);
        Assert.Contains("- 12 pieces", kitchen);
        Assert.Contains("+ 1x Cola", cashier);
        Assert.DoesNotContain("+ 2x Nuggets", cashier);
    }

    private static Order DeserializeOrder(string json) =>
        JsonSerializer.Deserialize<Order>(json, JsonOptions)
        ?? throw new InvalidOperationException("Offer-family fixture did not deserialize.");

    private static string ComposeCashier(OrderItem item, string? currency)
    {
        var builder = new StringBuilder();
        ReceiptComposer.AppendCashierItemLines(builder, item, depth: 0, spacing: 40, Labels, currency: currency);
        return builder.ToString();
    }

    private static string ComposeKitchen(OrderItem item)
    {
        var builder = new StringBuilder();
        ReceiptComposer.AppendKitchenItemLines(builder, item, depth: 0, Labels);
        return builder.ToString();
    }
}
