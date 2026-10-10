using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Shared ordering and duplicate-note rules for printable order-item trees.</summary>
public static class ReceiptItemDisplay
{
    /// <summary>Groups known section siblings by first occurrence; unscoped rows stay in place.</summary>
    public static IReadOnlyList<OrderItem> OrderItemsForDisplay(IEnumerable<OrderItem>? items)
    {
        var groups = new List<List<OrderItem>>();
        var sections = new Dictionary<string, List<OrderItem>>(StringComparer.Ordinal);
        var orderedItems = (items ?? Enumerable.Empty<OrderItem>())
            .Select((item, index) => new { Item = item, Index = index })
            .OrderBy(entry => RoleOrder(entry.Item.CompositionRole))
            .ThenBy(entry => entry.Item.PresentationOrder ?? int.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item);
        foreach (var item in orderedItems)
        {
            var sectionId = item.SectionId?.Trim();
            if (string.IsNullOrEmpty(sectionId))
            {
                groups.Add([item]);
                continue;
            }

            if (sections.TryGetValue(sectionId, out var group))
            {
                group.Add(item);
                continue;
            }

            var newGroup = new List<OrderItem> { item };
            sections.Add(sectionId, newGroup);
            groups.Add(newGroup);
        }

        return groups.SelectMany(group => group).ToList();
    }

    private static int RoleOrder(CompositionRole? role) => role switch
    {
        CompositionRole.Menu => 0,
        CompositionRole.Dish => 1,
        CompositionRole.RequiredChoice => 2,
        CompositionRole.Extra => 3,
        CompositionRole.Ingredient => 4,
        CompositionRole.Sauce => 5,
        CompositionRole.Unknown => 6,
        CompositionRole.Side => 7,
        CompositionRole.Drink => 8,
        _ => 9,
    };

    /// <summary>Suppresses a legacy note only when it repeats the chosen variation.</summary>
    public static string? DisplaySpecialInstructions(OrderItem item)
    {
        var instructions = item.SpecialInstructions?.Trim();
        if (string.IsNullOrEmpty(instructions))
            return null;

        var variation = item.VariationName?.Trim();
        return !string.IsNullOrEmpty(variation)
            && string.Equals(
                NormalizeDisplayText(instructions), NormalizeDisplayText(variation), StringComparison.OrdinalIgnoreCase)
                ? null
                : instructions;
    }

    private static string NormalizeDisplayText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
