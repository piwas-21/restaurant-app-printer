using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Kitchen routing over the nested item tree (backend PR #237 / issue #234 made OrderDto.Items
/// ROOT-ONLY — a bundle's components now exist only inside their parent's SideItems).
/// <para>The regression these guard: a "Menu Deal" whose product is FrontKitchen containing
/// BackKitchen fries. Routing off the top level alone saw no BackKitchen item, so the back kitchen
/// got NO ticket and the fries printed on the front kitchen's instead — a silently dropped order
/// line in a live restaurant.</para>
/// </summary>
public class KitchenTicketFilterTests
{
    private const string Front = "FrontKitchen";
    private const string Back = "BackKitchen";

    // ── single-kitchen bundle ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleKitchenBundle_GoesToThatKitchenOnly_ComponentsNested()
    {
        var items = new List<OrderItem>
        {
            Item("Menu Deal", Front, children:
            [
                Item("Kofte", Front),
                Item("Pide", Front),
            ]),
        };

        var front = KitchenTicketFilter.ItemsForKitchen(items, Front);
        var back = KitchenTicketFilter.ItemsForKitchen(items, Back);

        // One ticket, one root line, components still nested under their combo.
        var combo = Assert.Single(front);
        Assert.Equal("Menu Deal", combo.ProductName);
        Assert.Equal(new[] { "Kofte", "Pide" }, combo.SideItems!.Select(s => s.ProductName));

        // Each component exactly once across the whole ticket — not also promoted to a root line.
        Assert.Equal(new[] { "Menu Deal", "Kofte", "Pide" }, Flatten(front).Select(i => i.ProductName));

        // The kitchen with nothing to make gets no ticket at all.
        Assert.Empty(back);
    }

    [Fact]
    public void SingleKitchenBundle_KeepsComponentsWithNoKitchenOfTheirOwn()
    {
        // A drink or an extra sauce carries no KitchenType: it is a modifier of its parent, so it
        // rides with the parent rather than vanishing from every ticket.
        var items = new List<OrderItem>
        {
            Item("Menu Deal", Front, children:
            [
                Item("Kofte", Front),
                Item("Coke", kitchenType: null),
                Item("Extra sauce", kitchenType: "None"),
            ]),
        };

        var front = KitchenTicketFilter.ItemsForKitchen(items, Front);

        var combo = Assert.Single(front);
        Assert.Equal(
            new[] { "Kofte", "Coke", "Extra sauce" },
            combo.SideItems!.Select(s => s.ProductName));

        // They ride along as work this kitchen does, not as context — the ticket prints them as
        // real lines. "No KitchenType" is what makes this underivable from the item alone.
        Assert.All(Flatten(front), item => Assert.False(item.IsContextOnly));
    }

    // ── mixed-kitchen bundle (the reported failure) ─────────────────────────────────────────────

    [Fact]
    public void MixedKitchenBundle_BothKitchensGetATicket_EachComponentOnExactlyOne()
    {
        var items = new List<OrderItem>
        {
            Item("Menu Deal", Front, children:
            [
                Item("Kofte", Front),
                Item("Fries", Back, quantity: 2),
                Item("Coke", kitchenType: null),
            ]),
        };

        var front = KitchenTicketFilter.ItemsForKitchen(items, Front);
        var back = KitchenTicketFilter.ItemsForKitchen(items, Back);

        // Front makes the combo itself, so it keeps the combo and everything that is its own —
        // and the back kitchen's fries no longer ride along on its ticket.
        var frontCombo = Assert.Single(front);
        Assert.Equal("Menu Deal", frontCombo.ProductName);
        Assert.Equal(new[] { "Kofte", "Coke" }, frontCombo.SideItems!.Select(s => s.ProductName));
        Assert.All(Flatten(front), item => Assert.False(item.IsContextOnly));

        // Back gets a ticket at all (this is what was missing), carrying the combo line only as
        // context for the one component it makes.
        var backCombo = Assert.Single(back);
        Assert.Equal("Menu Deal", backCombo.ProductName);
        Assert.True(backCombo.IsContextOnly, "the back kitchen does not make the combo itself");
        var fries = Assert.Single(backCombo.SideItems!);
        Assert.Equal("Fries", fries.ProductName);
        Assert.Equal(2, fries.Quantity);
        Assert.False(fries.IsContextOnly);

        // Every component appears exactly once across the two tickets combined.
        var printed = Flatten(front).Concat(Flatten(back)).Select(i => i.ProductName).ToList();
        Assert.Equal(new[] { "Kofte", "Coke", "Fries" }, printed.Where(n => n != "Menu Deal"));
    }

    [Fact]
    public void MixedKitchenBundle_DoesNotMutateTheOrderItRoutes()
    {
        // The same tree is filtered once per kitchen, and the order is shared with the history list
        // and the UI — filtering has to copy.
        var combo = Item("Menu Deal", Front, children:
        [
            Item("Kofte", Front),
            Item("Fries", Back),
        ]);
        var items = new List<OrderItem> { combo };

        KitchenTicketFilter.ItemsForKitchen(items, Front);
        var back = KitchenTicketFilter.ItemsForKitchen(items, Back);

        Assert.Equal(new[] { "Kofte", "Fries" }, combo.SideItems!.Select(s => s.ProductName));
        Assert.Equal(new[] { "Fries" }, Assert.Single(back).SideItems!.Select(s => s.ProductName));
    }

    // ── depth, and the rest of the tree ─────────────────────────────────────────────────────────

    [Fact]
    public void NestedGrandchild_ReachesItsOwnKitchen_ThroughTheParentsAboveIt()
    {
        // The backend builds an arbitrary-depth tree (OrderItemFactory nests grandchildren), so a
        // one-level scan is not enough — the whole chain above a component may belong elsewhere.
        var items = new List<OrderItem>
        {
            Item("Feast Menu", Front, children:
            [
                Item("Burger Combo", Front, children:
                [
                    Item("Patty", Front),
                    Item("Fries", Back),
                ]),
            ]),
        };

        var back = KitchenTicketFilter.ItemsForKitchen(items, Back);
        var front = KitchenTicketFilter.ItemsForKitchen(items, Front);

        var backRoot = Assert.Single(back);
        Assert.Equal("Feast Menu", backRoot.ProductName);
        var backCombo = Assert.Single(backRoot.SideItems!);
        Assert.Equal("Burger Combo", backCombo.ProductName);
        Assert.Equal("Fries", Assert.Single(backCombo.SideItems!).ProductName);

        Assert.Equal(
            new[] { "Feast Menu", "Burger Combo", "Patty" },
            Flatten(front).Select(i => i.ProductName));
    }

    [Fact]
    public void PlainItems_RouteByTheirOwnKitchen_AsBefore()
    {
        var items = new List<OrderItem>
        {
            Item("Adana Kebab", Back),
            Item("Ayran", Front),
            Item("Napkins", kitchenType: null),
        };

        Assert.Equal(new[] { "Adana Kebab" }, KitchenTicketFilter.ItemsForKitchen(items, Back).Select(i => i.ProductName));
        Assert.Equal(new[] { "Ayran" }, KitchenTicketFilter.ItemsForKitchen(items, Front).Select(i => i.ProductName));
    }

    [Fact]
    public void OrderWithNothingForThisKitchen_YieldsNoTicket()
    {
        var items = new List<OrderItem> { Item("Ayran", Front) };

        Assert.Empty(KitchenTicketFilter.ItemsForKitchen(items, Back));
        Assert.Empty(KitchenTicketFilter.ItemsForKitchen(null, Back));
        Assert.Empty(KitchenTicketFilter.ItemsForKitchen(new List<OrderItem>(), Back));
    }

    [Fact]
    public void KitchenTypeMatchIsCaseInsensitive()
    {
        var items = new List<OrderItem> { Item("Fries", "backkitchen") };

        Assert.Equal("Fries", Assert.Single(KitchenTicketFilter.ItemsForKitchen(items, Back)).ProductName);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static OrderItem Item(
        string productName,
        string? kitchenType,
        int quantity = 1,
        List<OrderItem>? children = null) =>
        new()
        {
            Id = productName,
            ProductName = productName,
            KitchenType = kitchenType,
            Quantity = quantity,
            SideItems = children,
        };

    /// <summary>Every line a ticket would print, parents before their children.</summary>
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
