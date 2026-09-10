using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Selects the part of an order that one kitchen is responsible for, as a pure function over the
/// item tree — the same "extract the decidable part" shape as <see cref="FeedWatchdogDecision"/>
/// and <see cref="OrderFeedParser"/>.
/// <para>Backend PR #237 (issue #234) made <c>OrderDto.Items</c> ROOT-ONLY: a bundle's components
/// and an item's add-on sides are no longer top-level rows, they hang off their parent in
/// <see cref="OrderItem.SideItems"/>, to arbitrary depth. Every routing decision therefore has to
/// walk the whole tree. Reading only the top level meant a "Menu Deal" whose product is
/// FrontKitchen but which contains BackKitchen fries produced NO back-kitchen ticket at all, and
/// printed the fries on the front kitchen's ticket instead.</para>
/// </summary>
public static class KitchenTicketFilter
{
    /// <summary>Backend sentinel for "no kitchen prepares this line" (see OrderItemDto.KitchenType).</summary>
    private const string NoKitchen = "None";

    /// <summary>
    /// The item tree as <paramref name="kitchenType"/> should see it: every line this kitchen makes,
    /// each exactly once, still nested under its parent so a component reads as part of its combo.
    /// A parent this kitchen does NOT make is kept only when it has a matching descendant, and then
    /// only as context for it (the caller renders such a line differently — see
    /// <c>OrderPrintService.AppendKitchenItem</c>). Empty means "print nothing for this kitchen".
    /// </summary>
    public static List<OrderItem> ItemsForKitchen(IEnumerable<OrderItem>? items, string kitchenType)
    {
        if (items is null)
        {
            return new List<OrderItem>();
        }

        return items
            .Select(item => FilterItem(item, kitchenType, ridesWithParent: false))
            .OfType<OrderItem>()
            .ToList();
    }

    /// <param name="ridesWithParent">
    /// This item names no kitchen of its own and its parent is being made here, so it comes along.
    /// </param>
    /// <returns>The item with its children filtered, or null when nothing under it belongs here.</returns>
    private static OrderItem? FilterItem(OrderItem item, string kitchenType, bool ridesWithParent)
    {
        var madeHere = string.Equals(item.KitchenType, kitchenType, StringComparison.OrdinalIgnoreCase)
            || ridesWithParent;

        var children = new List<OrderItem>();
        foreach (var child in item.SideItems ?? Enumerable.Empty<OrderItem>())
        {
            // A child that names a kitchen is a dish, and is routed by that name — which is how a
            // BackKitchen component of a FrontKitchen combo reaches the back kitchen instead of
            // riding along on the front kitchen's ticket. A child that names none is a modifier of
            // its parent (an extra sauce, a drink): it belongs wherever the parent is being made,
            // and is dropped from tickets that only carry the parent as context.
            var childRidesAlong = madeHere && !DeclaresKitchen(child);
            var filteredChild = FilterItem(child, kitchenType, childRidesAlong);
            if (filteredChild is not null)
            {
                children.Add(filteredChild);
            }
        }

        if (!madeHere && children.Count == 0)
        {
            return null;
        }

        // A copy: the order instance is shared with the history list and the UI, and the other
        // kitchen's ticket filters the very same tree.
        var routed = item.WithSideItems(children);
        // Reaching here without madeHere means the line survived only to carry a component below
        // it. The renderer cannot work this out for itself — "no KitchenType" means "made wherever
        // the parent is", so a rode-along drink and a context-only combo both fail a plain
        // KitchenType comparison against this ticket's kitchen.
        routed.IsContextOnly = !madeHere;
        return routed;
    }

    /// <summary>
    /// Routes an order tree under an explicit tenant policy. This additive API is intentionally not
    /// wired into automatic printing yet: it establishes what a General/Default ticket contains
    /// without changing the legacy Front/Back print path.
    /// </summary>
    public static List<OrderItem> ItemsForDestination(
        IEnumerable<OrderItem>? items,
        KitchenRoutingPolicy policy,
        KitchenTicketDestination destination)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (items is null || !policy.Includes(destination))
        {
            return new List<OrderItem>();
        }

        if (policy.Mode == KitchenRoutingMode.SingleKitchen)
        {
            return items.Select(CloneForGeneralTicket).ToList();
        }

        // Roots inherit nothing: an unassigned root is Default work (see the routing matrix in
        // CASHIER-POS-REDESIGN-PLAN §9 — "unassigned" must never mean "silently omitted").
        return items
            .Select(item => FilterItemForStation(item, destination, inherited: KitchenTicketDestination.Default))
            .OfType<OrderItem>()
            .ToList();
    }

    private static OrderItem CloneForGeneralTicket(OrderItem item)
    {
        var children = item.SideItems?.Select(CloneForGeneralTicket).ToList();
        var routed = item.WithSideItems(children);
        routed.IsContextOnly = false;
        return routed;
    }

    /// <param name="inherited">
    /// The nearest ANCESTOR's effective destination. An unassigned line is its ancestor's work
    /// wherever that ancestor goes — an unassigned sauce on a Front combo is Front work, never
    /// independent Default work. Roots start at Default.
    /// </param>
    private static OrderItem? FilterItemForStation(
        OrderItem item,
        KitchenTicketDestination destination,
        KitchenTicketDestination inherited)
    {
        var assignment = KitchenRoutingPolicy.Classify(item.KitchenType);

        // An explicit station wins; unassigned inherits; an unknown value stays transparent for
        // inheritance but never prints as work — corrupted values surface as a routing error at
        // the pipeline layer instead of being silently reinterpreted as a station.
        var effective = assignment switch
        {
            KitchenAssignment.FrontKitchen => KitchenTicketDestination.FrontKitchen,
            KitchenAssignment.BackKitchen => KitchenTicketDestination.BackKitchen,
            _ => inherited,
        };
        var madeHere = assignment != KitchenAssignment.Unknown && effective == destination;

        var children = new List<OrderItem>();
        foreach (var child in item.SideItems ?? Enumerable.Empty<OrderItem>())
        {
            var filteredChild = FilterItemForStation(child, destination, effective);
            if (filteredChild is not null)
            {
                children.Add(filteredChild);
            }
        }

        if (!madeHere && children.Count == 0)
        {
            return null;
        }

        var routed = item.WithSideItems(children);
        routed.IsContextOnly = !madeHere;
        return routed;
    }

    /// <summary>True when the item names a kitchen of its own, rather than inheriting its parent's.</summary>
    private static bool DeclaresKitchen(OrderItem item) =>
        !string.IsNullOrWhiteSpace(item.KitchenType)
        && !string.Equals(item.KitchenType, NoKitchen, StringComparison.OrdinalIgnoreCase);
}
