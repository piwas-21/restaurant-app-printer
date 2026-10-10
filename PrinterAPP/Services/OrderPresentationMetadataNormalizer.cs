using PrinterAPP.Models;

namespace PrinterAPP.Services;

internal static class OrderPresentationMetadataNormalizer
{
    public static void Normalize(Order order)
    {
        foreach (var item in order.Items)
            Normalize(item);
    }

    public static void Normalize(PrinterFeedUpdate update)
    {
        foreach (var change in update.Changes)
        {
            Normalize(change.Previous);
            Normalize(change.Current);
        }
    }

    private static void Normalize(OrderItem? item)
    {
        if (item is null)
            return;
        item.QuantityBasis ??= QuantityBasis.Unknown;
        item.ConfigurationScope ??= ConfigurationScope.Unknown;
        item.CompositionRole ??= CompositionRole.Unknown;
        foreach (var ingredient in item.IngredientCustomizations ?? [])
        {
            ingredient.QuantityBasis ??= QuantityBasis.Unknown;
            ingredient.ConfigurationScope ??= ConfigurationScope.Unknown;
            ingredient.CompositionRole ??= CompositionRole.Unknown;
        }
        foreach (var child in item.SideItems ?? [])
            Normalize(child);
    }
}
