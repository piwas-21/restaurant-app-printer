using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>Pure policy coverage for issue #113's General/Default routing foundation.</summary>
public class KitchenRoutingPolicyTests
{
    [Fact]
    public void Stations_AllUnassignedRoots_GoToOneDefaultTicket_NotFrontOrBack()
    {
        var items = new List<OrderItem>
        {
            Item("None sentinel", "None"),
            Item("Missing", null),
            Item("Empty", string.Empty),
        };

        var defaultTicket = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.Default);

        Assert.Equal(new[] { "None sentinel", "Missing", "Empty" }, defaultTicket.Select(item => item.ProductName));
        Assert.Empty(KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.FrontKitchen));
        Assert.Empty(KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.BackKitchen));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("None")]
    [InlineData("none")]
    public void MissingAndNoneKitchenTypes_NormalizeToUnassigned(string? kitchenType)
    {
        var items = new[] { Item("Unassigned", kitchenType) };

        Assert.Equal(KitchenAssignment.Unassigned, KitchenRoutingPolicy.Classify(kitchenType));
        Assert.Equal("Unassigned", Assert.Single(KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.Default)).ProductName);
    }

    [Fact]
    public void Stations_FrontParentWithUnassignedChild_StaysOnFront()
    {
        var items = new[]
        {
            Item("Burger", "FrontKitchen", children: [Item("Extra sauce", "None")]),
        };

        var front = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.FrontKitchen);

        var burger = Assert.Single(front);
        Assert.Equal(new[] { "Burger", "Extra sauce" }, Flatten(front).Select(item => item.ProductName));
        Assert.False(burger.IsContextOnly);
        Assert.False(Assert.Single(burger.SideItems!).IsContextOnly);
        Assert.Empty(KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.Default));
    }

    [Fact]
    public void Stations_UnassignedParentWithBackChild_SplitsDefaultAndBackWithContext()
    {
        var items = new[]
        {
            Item("Combo", "None", children: [Item("Fries", "BackKitchen")]),
        };

        var defaultTicket = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.Default);
        var back = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.BackKitchen);

        var defaultRoot = Assert.Single(defaultTicket);
        Assert.Equal("Combo", defaultRoot.ProductName);
        Assert.Empty(defaultRoot.SideItems!);
        Assert.False(defaultRoot.IsContextOnly);

        var backRoot = Assert.Single(back);
        Assert.True(backRoot.IsContextOnly);
        var fries = Assert.Single(backRoot.SideItems!);
        Assert.Equal("Fries", fries.ProductName);
        Assert.False(fries.IsContextOnly);
    }

    [Fact]
    public void Stations_NestedUnassignedContext_PreservesTheFullPathToExplicitWork()
    {
        var items = new[]
        {
            Item("Meal", "None", children:
            [
                Item("Side bundle", null, children: [Item("Fries", "BackKitchen")]),
            ]),
        };

        var defaultTicket = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.Default);
        var back = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.BackKitchen);

        Assert.Equal(new[] { "Meal", "Side bundle" }, Flatten(defaultTicket).Select(item => item.ProductName));
        Assert.All(Flatten(defaultTicket), item => Assert.False(item.IsContextOnly));

        var meal = Assert.Single(back);
        var sideBundle = Assert.Single(meal.SideItems!);
        var fries = Assert.Single(sideBundle.SideItems!);
        Assert.Equal("Fries", fries.ProductName);
        Assert.True(meal.IsContextOnly);
        Assert.True(sideBundle.IsContextOnly);
        Assert.False(fries.IsContextOnly);
    }

    [Fact]
    public void SingleKitchen_GeneralTicketContainsEveryLineOnce_AndNoStationTickets()
    {
        var items = new[]
        {
            Item("Burger", "FrontKitchen", children:
            [
                Item("Sauce", null),
                Item("Fries", "BackKitchen"),
            ]),
        };

        var general = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.SingleKitchen, KitchenTicketDestination.General);

        Assert.Equal(new[] { "Burger", "Sauce", "Fries" }, Flatten(general).Select(item => item.ProductName));
        Assert.All(Flatten(general), item => Assert.False(item.IsContextOnly));
        Assert.Empty(KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.SingleKitchen, KitchenTicketDestination.FrontKitchen));
    }

    [Fact]
    public void Stations_ExplicitFrontAndBackKeepExistingBehavior()
    {
        var items = new[]
        {
            Item("Burger", "FrontKitchen", children: [Item("Fries", "BackKitchen")]),
        };

        var front = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.FrontKitchen);
        var back = KitchenTicketFilter.ItemsForDestination(
            items, KitchenRoutingPolicy.Stations, KitchenTicketDestination.BackKitchen);

        Assert.Equal(new[] { "Burger" }, Flatten(front).Select(item => item.ProductName));
        Assert.Equal(new[] { "Burger", "Fries" }, Flatten(back).Select(item => item.ProductName));
        Assert.True(Assert.Single(back).IsContextOnly);
    }

    [Fact]
    public void DestinationResolver_UsesConfiguredDefaultBeforeEveryFallback()
    {
        var result = KitchenDestinationResolver.Resolve(new(
            DefaultKitchenPrinterName: " default-printer ",
            KitchenPrinterName: "legacy-printer",
            FrontKitchenPrinterName: "front-printer",
            BackKitchenPrinterName: "back-printer"));

        Assert.Equal(KitchenDestinationResolutionKind.ConfiguredDefault, result.Kind);
        Assert.Equal("default-printer", result.PrinterName);
        Assert.True(result.IsConfigured);
    }

    [Fact]
    public void DestinationResolver_UsesLegacyKitchenPrinterBeforeStationFallback()
    {
        var result = KitchenDestinationResolver.Resolve(new(
            DefaultKitchenPrinterName: " ",
            KitchenPrinterName: "legacy-printer",
            FrontKitchenPrinterName: "front-printer",
            BackKitchenPrinterName: null));

        Assert.Equal(KitchenDestinationResolutionKind.LegacyKitchenPrinter, result.Kind);
        Assert.Equal("legacy-printer", result.PrinterName);
    }

    [Theory]
    [InlineData("front-printer", null, "front-printer")]
    [InlineData(null, "back-printer", "back-printer")]
    [InlineData("shared-printer", " SHARED-PRINTER ", "shared-printer")]
    public void DestinationResolver_UsesTheOnlyDistinctConfiguredStation(
        string? frontPrinter,
        string? backPrinter,
        string expectedPrinter)
    {
        var result = KitchenDestinationResolver.Resolve(new(
            DefaultKitchenPrinterName: null,
            KitchenPrinterName: null,
            FrontKitchenPrinterName: frontPrinter,
            BackKitchenPrinterName: backPrinter));

        Assert.Equal(KitchenDestinationResolutionKind.SoleConfiguredStation, result.Kind);
        Assert.Equal(expectedPrinter, result.PrinterName, ignoreCase: true);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("front-printer", "back-printer")]
    public void DestinationResolver_DoesNotGuessWhenNoUniqueDestinationExists(
        string? frontPrinter,
        string? backPrinter)
    {
        var result = KitchenDestinationResolver.Resolve(new(
            DefaultKitchenPrinterName: null,
            KitchenPrinterName: null,
            FrontKitchenPrinterName: frontPrinter,
            BackKitchenPrinterName: backPrinter));

        Assert.Equal(KitchenDestinationResolutionKind.NotConfigured, result.Kind);
        Assert.Null(result.PrinterName);
        Assert.False(result.IsConfigured);
    }

    private static OrderItem Item(string productName, string? kitchenType, List<OrderItem>? children = null) =>
        new()
        {
            Id = productName,
            ProductName = productName,
            KitchenType = kitchenType,
            SideItems = children,
        };

    private static IEnumerable<OrderItem> Flatten(IEnumerable<OrderItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.SideItems ?? Enumerable.Empty<OrderItem>()))
            {
                yield return child;
            }
        }
    }
}
