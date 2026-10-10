using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class ReceiptProjectionGoldenTests
{
    private const string GoldenOrderFeed = """
        {"success":true,"data":{"projectionVersion":2,"items":[
          {"id":"10000000-0000-0000-0000-000000000001","orderNumber":"GOLDEN-1","type":"TakeAway","orderDate":"2026-10-10T10:00:00Z","currency":"EUR","total":35,"totalPaid":0,"remainingAmount":35,"items":[
            {"id":"20000000-0000-0000-0000-000000000001","productName":"Lunch Menu","quantity":2,"unitPrice":14,"itemTotal":28,"kitchenType":"FrontKitchen","compositionRole":"Menu","quantityBasis":"LineTotal","configurationScope":"SharedAcrossParentUnits","presentationOrder":0,"sideItems":[
              {"id":"20000000-0000-0000-0000-000000000020","productName":"Cola","quantity":1,"itemTotal":0,"kitchenType":"FrontKitchen","kind":"SideItem","quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Drink","presentationOrder":50},
              {"id":"20000000-0000-0000-0000-000000000012","productName":"Steak","variationName":"Medium","quantity":2,"itemTotal":0,"kitchenType":"BackKitchen","kind":"BundleChild","quantityBasis":"LineTotal","configurationScope":"Unknown","compositionRole":"RequiredChoice","sectionId":"meat","presentationOrder":2,"menuSectionItemId":"30000000-0000-0000-0000-000000000002","parentComponentOrderItemId":"20000000-0000-0000-0000-000000000010"},
              {"id":"20000000-0000-0000-0000-000000000021","productName":"Fries","quantity":2,"itemTotal":0,"kitchenType":"FrontKitchen","kind":"SideItem","suggestedSideItemId":"30000000-0000-0000-0000-000000000099","quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Side","presentationOrder":40},
              {"id":"20000000-0000-0000-0000-000000000022","productName":"Legacy Dip","quantity":3,"itemTotal":0,"kind":"SideItem","quantityBasis":"Unknown","configurationScope":"Unknown","compositionRole":"Unknown","presentationOrder":55},
              {"id":"20000000-0000-0000-0000-000000000010","productName":"Taco selection","quantity":1,"itemTotal":0,"kitchenType":"FrontKitchen","compositionRole":"Dish","presentationLabel":"Taco","suggestedSideItemId":"30000000-0000-0000-0000-000000000099","quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","presentationOrder":1,"specialInstructions":"No cilantro","ingredientCustomizations":[
                {"ingredientId":"40000000-0000-0000-0000-000000000001","ingredientName":"Cheddar","quantity":2,"isRemoved":false,"isAddOn":true,"quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Ingredient","presentationOrder":3},
                {"ingredientId":"40000000-0000-0000-0000-000000000002","ingredientName":"Onion","quantity":0,"isRemoved":true,"isAddOn":false,"quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Ingredient","presentationOrder":4},
                {"ingredientId":"40000000-0000-0000-0000-000000000001","ingredientName":"Cheddar","quantity":1,"isRemoved":false,"isAddOn":true,"quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Ingredient","presentationOrder":2},
                {"ingredientId":"40000000-0000-0000-0000-000000000003","ingredientName":"Chili","quantity":0,"isRemoved":false,"isAddOn":true,"quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Ingredient","presentationOrder":5}],"sideItems":[]},
              {"id":"20000000-0000-0000-0000-000000000011","productName":"Kebab","quantity":4,"itemTotal":0,"kitchenType":"BackKitchen","kind":"BundleChild","quantityBasis":"LineTotal","configurationScope":"SharedAcrossParentUnits","compositionRole":"RequiredChoice","sectionId":"meat","presentationOrder":1,"menuSectionItemId":"30000000-0000-0000-0000-000000000001","parentComponentOrderItemId":"20000000-0000-0000-0000-000000000010"},
              {"id":"20000000-0000-0000-0000-000000000024","productName":"Extra Lamb","quantity":3,"itemTotal":0,"kitchenType":"BackKitchen","kind":"BundleChild","quantityBasis":"LineTotal","configurationScope":"SharedAcrossParentUnits","compositionRole":"Extra","sectionId":"extra-meat","presentationOrder":3,"parentComponentOrderItemId":"20000000-0000-0000-0000-000000000010"},
              {"id":"20000000-0000-0000-0000-000000000023","productName":"Harissa","quantity":1,"itemTotal":0,"kind":"SideItem","quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Sauce","presentationOrder":30,"parentComponentOrderItemId":"20000000-0000-0000-0000-000000000010"}]},
            {"id":"20000000-0000-0000-0000-000000000002","productName":"Lunch Menu","quantity":1,"unitPrice":7,"itemTotal":7,"kitchenType":"FrontKitchen","compositionRole":"Menu","quantityBasis":"LineTotal","configurationScope":"SharedAcrossParentUnits","presentationOrder":1,"sideItems":[
              {"id":"20000000-0000-0000-0000-000000000030","productName":"Taco selection","quantity":1,"itemTotal":0,"kitchenType":"FrontKitchen","compositionRole":"Dish","presentationLabel":"Taco","quantityBasis":"PerParentUnit","configurationScope":"IndependentParentUnits","presentationOrder":1,"sideItems":[
                {"id":"20000000-0000-0000-0000-000000000031","productName":"Mango sauce","quantity":1,"itemTotal":0,"quantityBasis":"PerParentUnit","configurationScope":"IndependentParentUnits","compositionRole":"Sauce","presentationOrder":1}]}]},
            {"id":"20000000-0000-0000-0000-000000000003","productName":"Lunch Menu","quantity":3,"unitPrice":5,"itemTotal":15,"kitchenType":"FrontKitchen","compositionRole":"Menu","quantityBasis":"LineTotal","configurationScope":"SharedAcrossParentUnits","presentationOrder":2,"sideItems":[
              {"id":"20000000-0000-0000-0000-000000000040","productName":"Still Water","quantity":1,"itemTotal":0,"kind":"SideItem","quantityBasis":"PerParentUnit","configurationScope":"SharedAcrossParentUnits","compositionRole":"Drink","presentationOrder":50}]}
          ]}],"updates":[]}}
        """;

    [Fact]
    public void V2_serialized_feed_preserves_ownership_roles_and_parent_only_prices_at_both_widths()
    {
        var parsed = OrderFeedParser.Parse(GoldenOrderFeed);
        Assert.True(parsed.IsSuccess, parsed.FailureMessage);
        Assert.Equal(2, parsed.ProjectionVersion);
        Assert.Empty(parsed.Errors);
        var order = Assert.Single(parsed.Orders);
        var roots = ReceiptCompositionProjection.Build(order.Items);
        Assert.Equal(3, roots.Count);
        Assert.NotSame(order.Items[0], roots[0]);

        var taco = Assert.Single(roots[0].SideItems!, item => item.CompositionRole == CompositionRole.Dish);
        Assert.Equal(Guid.Parse("30000000-0000-0000-0000-000000000099"),
            Assert.Single(roots[0].SideItems!, item => item.ProductName == "Fries").SuggestedSideItemId);
        Assert.Equal(new[] { "Kebab", "Steak" }, taco.SideItems!.Where(item => item.CompositionRole == CompositionRole.RequiredChoice)
            .Select(item => item.ProductName));
        Assert.Equal(new[] { 2, 1, 3 }, roots.Select(item => item.Quantity));
        Assert.Equal(3, roots.Count(item => item.ProductName == "Lunch Menu"));

        foreach (var widthMillimeters in new[] { 58, 80 })
        {
            var cashier = ComposeCashier(roots, widthMillimeters);
            Assert.Contains("2x Lunch Menu", cashier);
            Assert.Contains("1x Lunch Menu", cashier);
            Assert.Contains("3x Lunch Menu", cashier);
            Assert.Contains("1x Taco (each)", cashier);
            Assert.Contains("1x Mango sauce", cashier);
            Assert.DoesNotContain("independent configurations", cashier);
            Assert.Contains("1x Still Water (each)", cashier);
            Assert.Contains("4x Kebab", cashier);
            Assert.Contains("2x Steak (Medium)", cashier);
            Assert.Contains("+ 3x Extra Lamb", cashier);
            Assert.DoesNotContain("8x Kebab", cashier);
            Assert.Contains("3x Legacy Dip", cashier);
            Assert.Equal(2, Count(cashier, "Cheddar"));
            Assert.True(cashier.IndexOf("+ Cheddar", StringComparison.Ordinal)
                < cashier.IndexOf("+ 2x Cheddar", StringComparison.Ordinal));
            Assert.Contains("NO Onion", cashier);
            Assert.DoesNotContain("Taco selection", cashier);
            Assert.DoesNotContain("+ 1x Taco", cashier);
            Assert.DoesNotContain("Chili", cashier);
            Assert.True(cashier.IndexOf("Fries", StringComparison.Ordinal)
                < cashier.IndexOf("Cola", StringComparison.Ordinal));
            Assert.True(cashier.IndexOf("Harissa", StringComparison.Ordinal)
                < cashier.IndexOf("Cola", StringComparison.Ordinal));
            Assert.True(cashier.IndexOf("Cheddar", StringComparison.Ordinal)
                < cashier.IndexOf("Harissa", StringComparison.Ordinal));
            Assert.True(cashier.IndexOf("4x Kebab", StringComparison.Ordinal)
                < cashier.IndexOf("Cheddar", StringComparison.Ordinal));
            Assert.True(cashier.IndexOf("Legacy Dip", StringComparison.Ordinal)
                < cashier.IndexOf("Fries", StringComparison.Ordinal));
            Assert.Equal(1, Count(cashier, "EUR 28.00"));
            Assert.Equal(1, Count(cashier, "EUR 7.00"));
            Assert.DoesNotContain("EUR 0.00", cashier);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var pc857 = Encoding.GetEncoding(857, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var encodedTicket = pc857.GetString(pc857.GetBytes(ComposeCashier(roots, 80)));
        Assert.Contains("4x Kebab", encodedTicket);
    }

    [Fact]
    public void Frozen_station_links_keep_required_meats_on_back_ticket_and_dishes_on_front()
    {
        var order = Assert.Single(OrderFeedParser.Parse(GoldenOrderFeed).Orders);
        var roots = ReceiptCompositionProjection.Build(order.Items);
        var front = KitchenTicketFilter.SelectionForDestination(roots, KitchenRoutingPolicy.Stations,
            KitchenTicketDestination.FrontKitchen);
        var back = KitchenTicketFilter.SelectionForDestination(roots, KitchenRoutingPolicy.Stations,
            KitchenTicketDestination.BackKitchen);

        Assert.Contains("Taco", ComposeKitchen(front.Items));
        Assert.DoesNotContain("Kebab", ComposeKitchen(front.Items));
        Assert.Contains("4x Kebab", ComposeKitchen(back.Items));
        Assert.Contains("2x Steak", ComposeKitchen(back.Items));
        Assert.Contains("- Medium", ComposeKitchen(back.Items));
        Assert.Contains("+ 3x Extra Lamb", ComposeKitchen(back.Items));
        Assert.DoesNotContain("Extra Lamb", ComposeKitchen(front.Items));
        Assert.DoesNotContain("Cola", ComposeKitchen(back.Items));
    }

    [Fact]
    public void Legacy_v1_feed_keeps_side_item_count_exact_when_scope_is_missing()
    {
        const string legacyFeed = """
            {"data":{"items":[{"orderNumber":"V1","items":[{"productName":"Menu","quantity":3,"ingredientCustomizations":[{"ingredientName":"Cheddar","quantity":1}],"sideItems":[{"productName":"Fries","quantity":2,"kind":"SideItem"}]}]}]}}
            """;
        var parsed = OrderFeedParser.Parse(legacyFeed);
        var order = Assert.Single(parsed.Orders);
        var receipt = ComposeCashier(ReceiptCompositionProjection.Build(order.Items), 58);

        Assert.Null(parsed.ProjectionVersion);
        Assert.Contains("2x Fries", receipt);
        Assert.DoesNotContain("6x Fries", receipt);
        Assert.Contains("+ Cheddar", receipt);
        Assert.DoesNotContain("Recorded; scope unknown", receipt);
    }

    [Fact]
    public void V2_null_metadata_keeps_exact_quantities_without_scope_explanations()
    {
        const string feed = """
            {"data":{"projectionVersion":2,"items":[{"orderNumber":"V2-UNKNOWN","items":[
              {"productName":"Menu","quantity":3,"sideItems":[
                {"productName":"Dip","quantity":2,"kind":"SideItem"}],
                "ingredientCustomizations":[
                  {"ingredientName":"Onion","quantity":0,"isRemoved":true},
                  {"ingredientName":"Cheddar","quantity":1,"isRemoved":false}]}]}]}}
            """;
        var parsed = OrderFeedParser.Parse(feed);
        var root = Assert.Single(Assert.Single(parsed.Orders).Items);
        var dip = Assert.Single(root.SideItems!);
        Assert.Equal(QuantityBasis.Unknown, dip.QuantityBasis);
        Assert.Equal(ConfigurationScope.Unknown, dip.ConfigurationScope);
        Assert.All(root.IngredientCustomizations!, ingredient =>
        {
            Assert.Equal(CompositionRole.Unknown, ingredient.CompositionRole);
            Assert.Equal(ConfigurationScope.Unknown, ingredient.ConfigurationScope);
        });

        var receipt = ComposeCashier([root], 58);
        Assert.Contains("2x Dip", receipt);
        Assert.DoesNotContain("6x Dip", receipt);
        Assert.Contains("NO Onion", receipt);
        Assert.Contains("+ Cheddar", receipt);
        Assert.DoesNotContain("Recorded", receipt);
        Assert.DoesNotContain("scope unknown", receipt);
    }

    [Fact]
    public void Feed_request_opts_into_projection_v2_without_changing_existing_cursors()
    {
        var baseUri = new UriBuilder(Uri.UriSchemeHttps, "printer.example").Uri;
        var uri = new Uri(EventStreamingService.BuildFeedUrl(
            baseUri.ToString(), DateTime.UnixEpoch, "fr-CH", "opaque-update", "opaque-order"));
        Assert.Contains("projectionVersion=2", uri.Query, StringComparison.Ordinal);
        Assert.Contains("updateCursor=opaque-update", uri.Query, StringComparison.Ordinal);
        Assert.Contains("orderCursor=opaque-order", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_correction_snapshot_keeps_metadata_and_composes_explicit_component_scope()
    {
        var change = new PrinterFeedChange
        {
            Kind = KitchenChangeKind.Add,
            Current = new OrderItem
            {
                Id = "50000000-0000-0000-0000-000000000001",
                ProductName = "Lunch Menu",
                Quantity = 2,
                QuantityBasis = QuantityBasis.LineTotal,
                ConfigurationScope = ConfigurationScope.SharedAcrossParentUnits,
                CompositionRole = CompositionRole.Menu,
                SideItems =
                [
                    new OrderItem
                    {
                        Id = "50000000-0000-0000-0000-000000000002",
                        ProductName = "Taco selection",
                        Quantity = 1,
                        QuantityBasis = QuantityBasis.PerParentUnit,
                        ConfigurationScope = ConfigurationScope.SharedAcrossParentUnits,
                        CompositionRole = CompositionRole.Dish,
                        PresentationLabel = "Taco",
                        SideItems =
                        [
                            new OrderItem
                            {
                                Id = "50000000-0000-0000-0000-000000000004",
                                ProductName = "Fries",
                                Quantity = 1,
                                QuantityBasis = QuantityBasis.PerParentUnit,
                                ConfigurationScope = ConfigurationScope.SharedAcrossParentUnits,
                                CompositionRole = CompositionRole.Side,
                                SuggestedSideItemId = Guid.Parse("30000000-0000-0000-0000-000000000099"),
                            },
                            new OrderItem
                            {
                                Id = "50000000-0000-0000-0000-000000000003",
                                ProductName = "Kebab",
                                Quantity = 4,
                                QuantityBasis = QuantityBasis.LineTotal,
                                CompositionRole = CompositionRole.RequiredChoice,
                                SectionId = "meat",
                                ParentComponentOrderItemId = Guid.Parse("50000000-0000-0000-0000-000000000002"),
                            },
                        ],
                    },
                ],
            },
        };
        var update = new PrinterFeedUpdate
        {
            JobId = Guid.Parse("60000000-0000-0000-0000-000000000001"),
            Revision = 1,
            Target = DevicePrintTarget.FrontKitchen,
            OrderId = Guid.Parse("60000000-0000-0000-0000-000000000002"),
            OrderNumber = "FIX-1",
            Audience = "Kitchen",
            CreatedAt = DateTime.UtcNow,
            Changes = [change],
        };
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { projectionVersion = 2, items = Array.Empty<object>(), updates = new[] { update }, nextUpdateCursor = "fix-cursor" },
        }, jsonOptions);

        var parsed = OrderFeedParser.Parse(body);
        Assert.True(parsed.IsSuccess, parsed.FailureMessage);
        Assert.Equal(2, parsed.ProjectionVersion);
        var snapshot = Assert.Single(Assert.Single(Assert.Single(parsed.Updates).Changes).Current!.SideItems!).SideItems;
        Assert.Equal(QuantityBasis.LineTotal, Assert.Single(snapshot!, item => item.ProductName == "Kebab").QuantityBasis);
        Assert.Equal(Guid.Parse("30000000-0000-0000-0000-000000000099"),
            Assert.Single(snapshot!, item => item.ProductName == "Fries").SuggestedSideItemId);

        var ingredient = new IngredientCustomization { IngredientName = "Onion", Quantity = 0, IsRemoved = true };
        var wireSnapshot = new OrderItem
        {
            Id = "70000000-0000-0000-0000-000000000001",
            ProductName = "Taco",
            Quantity = 1,
            IngredientCustomizations = [ingredient],
        };
        var ingredientFeed = JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                projectionVersion = 2,
                items = Array.Empty<object>(),
                updates = new[]
                {
                    new PrinterFeedUpdate
                    {
                        JobId = Guid.Parse("60000000-0000-0000-0000-000000000003"),
                        Revision = 1,
                        Target = DevicePrintTarget.FrontKitchen,
                        OrderId = Guid.Parse("60000000-0000-0000-0000-000000000004"),
                        OrderNumber = "FIX-2",
                        Audience = "Kitchen",
                        CreatedAt = DateTime.UtcNow,
                        Changes = [new PrinterFeedChange { Kind = KitchenChangeKind.Add, Current = wireSnapshot }],
                    },
                },
                nextUpdateCursor = "fix-cursor-2",
            },
        }, jsonOptions);
        var parsedIngredientUpdate = OrderFeedParser.Parse(ingredientFeed);
        var normalizedIngredient = Assert.Single(Assert.Single(Assert.Single(parsedIngredientUpdate.Updates).Changes).Current!.IngredientCustomizations!);
        Assert.Equal(QuantityBasis.Unknown, normalizedIngredient.QuantityBasis);
        Assert.Equal(ConfigurationScope.Unknown, normalizedIngredient.ConfigurationScope);

        var receipt = UpdateReceiptComposer.Compose(Assert.Single(parsed.Updates));
        Assert.Contains("Taco", receipt);
        Assert.Contains("4x Kebab", receipt);
        Assert.DoesNotContain("8x Kebab", receipt);
    }

    private static string ComposeCashier(IEnumerable<OrderItem> items, int paperWidthMillimeters)
    {
        var builder = new StringBuilder();
        var characterColumns = paperWidthMillimeters == 80 ? 48 : 32;
        foreach (var item in items)
            ReceiptComposer.AppendCashierItemLines(builder, item, 0, characterColumns,
                PrintLabelCatalog.English, currency: "EUR");
        return builder.ToString();
    }

    private static string ComposeKitchen(IEnumerable<OrderItem> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
            ReceiptComposer.AppendKitchenItemLines(builder, item, 0, PrintLabelCatalog.English);
        return builder.ToString();
    }

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
