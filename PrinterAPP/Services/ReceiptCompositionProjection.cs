using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Builds a receipt-only tree from explicit frozen component ownership metadata.</summary>
public static class ReceiptCompositionProjection
{
    public static List<OrderItem> Build(IEnumerable<OrderItem>? items)
    {
        var roots = new List<OrderItem>();
        var nodes = new List<ItemNode>();
        var byId = new Dictionary<Guid, ItemNode>();
        foreach (var item in items ?? Enumerable.Empty<OrderItem>())
            Clone(item, roots, parent: null, nodes, byId);

        foreach (var node in nodes)
        {
            if (node.Item.ParentComponentOrderItemId is not { } parentId
                || !byId.TryGetValue(parentId, out var parent)
                || ReferenceEquals(node, parent)
                || WouldCreateCycle(node, parent))
                continue;

            node.Container.Remove(node.Item);
            parent.Item.SideItems ??= [];
            parent.Item.SideItems.Add(node.Item);
            node.Parent = parent;
            node.Container = parent.Item.SideItems;
        }

        foreach (var root in roots)
            SortChildren(root);
        return roots;
    }

    public static OrderItem BuildSingle(OrderItem item) => Build([item])[0];

    private static void Clone(
        OrderItem source,
        List<OrderItem> container,
        ItemNode? parent,
        ICollection<ItemNode> nodes,
        IDictionary<Guid, ItemNode> byId)
    {
        var item = source.WithSideItems(source.SideItems is null ? null : []);
        container.Add(item);
        var node = new ItemNode(item, container, parent);
        nodes.Add(node);
        if (Guid.TryParse(item.Id, out var id))
            byId.TryAdd(id, node);

        if (source.SideItems is null)
            return;
        foreach (var child in source.SideItems)
            Clone(child, item.SideItems!, node, nodes, byId);
    }

    private static bool WouldCreateCycle(ItemNode node, ItemNode proposedParent)
    {
        for (var ancestor = proposedParent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, node))
                return true;
        }
        return false;
    }

    private static void SortChildren(OrderItem item)
    {
        if (item.SideItems is null)
            return;
        item.SideItems = ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems).ToList();
        foreach (var child in item.SideItems)
            SortChildren(child);
    }

    private sealed class ItemNode(OrderItem item, List<OrderItem> container, ItemNode? parent)
    {
        public OrderItem Item { get; } = item;
        public List<OrderItem> Container { get; set; } = container;
        public ItemNode? Parent { get; set; } = parent;
    }
}
