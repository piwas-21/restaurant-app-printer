using PrinterAPP.Models;

namespace PrinterAPP.Services;

internal static class ReceiptComponentFormatting
{
    public static string DishHeading(OrderItem item, PrintLabels labels, string? ownerLabel, int menuQuantity)
    {
        var heading = $"{item.PresentationLabel} ×{item.Quantity}";
        var basisHeading = item.QuantityBasis switch
        {
            QuantityBasis.PerParentUnit when item.ConfigurationScope is ConfigurationScope.SharedAcrossParentUnits
                => $"{item.PresentationLabel}: {PrintLabelCatalog.ForEachLabel(labels, ownerLabel ?? item.ProductName)} ×{item.Quantity}",
            QuantityBasis.PerParentUnit
                => $"{item.PresentationLabel}: {labels.RecordedScopeUnknown} ×{item.Quantity}",
            QuantityBasis.LineTotal => $"{PrintLabelCatalog.TotalForMenusLabel(labels, menuQuantity)}: {heading}",
            _ => $"{heading}: {labels.RecordedScopeUnknown}",
        };
        return basisHeading + ScopeNote(item.ConfigurationScope, labels);
    }

    public static string? ComponentLabel(OrderItem item) =>
        item.CompositionRole == CompositionRole.Dish && !string.IsNullOrWhiteSpace(item.PresentationLabel)
            ? item.PresentationLabel : null;

    public static int DisplayQuantity(OrderItem item, int parentQuantity) => item.QuantityBasis switch
    {
        null when string.Equals(item.Kind, "SideItem", StringComparison.OrdinalIgnoreCase) => item.Quantity * parentQuantity,
        _ => item.Quantity,
    };

    public static bool HasKnownPerParentScope(QuantityBasis? basis, ConfigurationScope? scope) =>
        basis == QuantityBasis.PerParentUnit && scope == ConfigurationScope.SharedAcrossParentUnits;

    public static string ScopeNote(ConfigurationScope? scope, PrintLabels labels) => scope switch
    {
        ConfigurationScope.Unknown => $" ({labels.ConfigurationScopeUnknown})",
        ConfigurationScope.IndependentParentUnits => $" ({labels.IndependentConfiguration})",
        _ => string.Empty,
    };

    public static List<OrderItem> LineTotalChoiceGroup(IReadOnlyList<OrderItem> items, OrderItem candidate)
    {
        if (candidate.CompositionRole != CompositionRole.RequiredChoice
            || candidate.QuantityBasis != QuantityBasis.LineTotal
            || string.IsNullOrWhiteSpace(candidate.SectionId))
            return [];
        return items.Where(item => item.CompositionRole == CompositionRole.RequiredChoice
                && item.QuantityBasis == QuantityBasis.LineTotal
                && string.Equals(item.SectionId, candidate.SectionId, StringComparison.Ordinal))
            .ToList();
    }

    public static string LineTotalChoiceLabel(OrderItem item, PrintLabels labels)
    {
        var name = string.IsNullOrWhiteSpace(item.VariationName)
            ? item.ProductName : $"{item.ProductName} ({item.VariationName})";
        return $"{name} ×{item.Quantity}{ScopeNote(item.ConfigurationScope, labels)}";
    }
}
