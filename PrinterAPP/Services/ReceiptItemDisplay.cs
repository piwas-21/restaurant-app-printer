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
        foreach (var item in items ?? Enumerable.Empty<OrderItem>())
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
