using PrinterAPP.Models;

namespace PrinterAPP.Services;

internal static class ReceiptComponentFormatting
{
    public static string DishHeading(OrderItem item, PrintLabels labels, int parentQuantity) =>
        string.Concat(item.Quantity, "x ", item.PresentationLabel, QuantityScopeSuffix(item, parentQuantity, labels));

    public static string QuantityScopeSuffix(OrderItem item, int parentQuantity, PrintLabels labels) =>
        HasKnownPerParentScope(item.QuantityBasis, item.ConfigurationScope) && parentQuantity > 1
            ? string.Concat(" (", labels.Each, ")")
            : string.Empty;

    public static string QuantityScopeSuffix(IngredientCustomization item, int parentQuantity, PrintLabels labels) =>
        HasKnownPerParentScope(item.QuantityBasis, item.ConfigurationScope) && parentQuantity > 1
            ? string.Concat(" (", labels.Each, ")")
            : string.Empty;

    public static bool HasKnownPerParentScope(QuantityBasis? basis, ConfigurationScope? scope) =>
        basis == QuantityBasis.PerParentUnit && scope == ConfigurationScope.SharedAcrossParentUnits;
}
