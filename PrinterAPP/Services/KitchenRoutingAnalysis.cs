using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>An unresolved KitchenType and the destination it would have inherited.</summary>
public sealed record KitchenRoutingIssue(
    string ProductName,
    string? KitchenType,
    KitchenTicketDestination Destination);

/// <summary>Filtered ticket plus unresolved leaves/roots found while walking the same tree.</summary>
public sealed record KitchenRoutingSelection(
    IReadOnlyList<OrderItem> Items,
    IReadOnlyList<KitchenRoutingIssue> UnknownItems)
{
    public bool HasUnknown => UnknownItems.Count > 0;
}

/// <summary>
/// Pure analysis for kitchen routing. Unknown values are never converted to a station; known leaves
/// below an unknown root remain routable, while the unresolved line is surfaced to the pipeline.
/// </summary>
public static class KitchenRoutingAnalysis
{
    public static KitchenRoutingSelection ForDestination(
        IEnumerable<OrderItem>? items,
        KitchenRoutingPolicy policy,
        KitchenTicketDestination destination)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var source = items?.ToList() ?? new List<OrderItem>();
        if (!policy.Includes(destination))
            return new(Array.Empty<OrderItem>(), Array.Empty<KitchenRoutingIssue>());

        var unknown = new List<KitchenRoutingIssue>();
        if (policy.Mode == KitchenRoutingMode.SingleKitchen)
        {
            // SingleKitchen intentionally owns every line. KitchenType is irrelevant here and must
            // not turn a configured one-printer venue into an unknown route.
            return new(KitchenTicketFilter.ItemsForDestination(source, policy, destination), unknown);
        }

        foreach (var root in source)
        {
            CollectUnknown(root, KitchenTicketDestination.Default, unknown);
        }

        return new(
            KitchenTicketFilter.ItemsForDestination(source, policy, destination),
            unknown.Where(issue => issue.Destination == destination).ToList());
    }

    /// <summary>All unknown kitchen assignments, independent of a physical destination.</summary>
    public static IReadOnlyList<KitchenRoutingIssue> UnknownItems(IEnumerable<OrderItem>? items)
    {
        var result = new List<KitchenRoutingIssue>();
        foreach (var item in items ?? Enumerable.Empty<OrderItem>())
            CollectUnknown(item, KitchenTicketDestination.Default, result);
        return result;
    }

    private static void CollectUnknown(
        OrderItem item,
        KitchenTicketDestination inherited,
        ICollection<KitchenRoutingIssue> result)
    {
        var assignment = KitchenRoutingPolicy.Classify(item.KitchenType);
        var effective = assignment switch
        {
            KitchenAssignment.FrontKitchen => KitchenTicketDestination.FrontKitchen,
            KitchenAssignment.BackKitchen => KitchenTicketDestination.BackKitchen,
            _ => inherited,
        };

        if (assignment == KitchenAssignment.Unknown)
        {
            result.Add(new KitchenRoutingIssue(item.ProductName, item.KitchenType, inherited));
        }

        foreach (var child in item.SideItems ?? Enumerable.Empty<OrderItem>())
            CollectUnknown(child, effective, result);
    }
}
