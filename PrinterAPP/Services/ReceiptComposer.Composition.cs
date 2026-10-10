using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public static partial class ReceiptComposer
{
    private sealed record CashierPrintContext(PrintLabels Labels, int Spacing, string? Currency);

    private sealed record KitchenPrintContext(PrintLabels Labels, PrintStyleSettings? Styles);

    private readonly record struct DetailLinesContext(
        PrintLabels Labels, int ParentQuantity, SectionStyle? Style, bool TallEmphasis);

    public static void AppendCashierItemLines(
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels, int parentQuantity = 1,
        string? currency = null)
    {
        if (depth == 0)
            item = ReceiptCompositionProjection.BuildSingle(item);
        AppendCashierProjected(sb, item, depth, new CashierPrintContext(labels, spacing, currency), parentQuantity);
    }

    private static void AppendCashierProjected(
        StringBuilder sb, OrderItem item, int depth, CashierPrintContext context, int parentQuantity)
    {
        var indent = new string(' ', depth * 3);
        var name = string.IsNullOrWhiteSpace(item.VariationName)
            ? item.ProductName : $"{item.ProductName} ({item.VariationName})";

        if (depth == 0)
        {
            var itemLine = $"{item.Quantity}x {name}";
            var price = Money(item.ItemTotal, context.Currency);
            sb.Append(itemLine);
            sb.Append(new string('.', Math.Max(1, context.Spacing - itemLine.Length - price.Length)));
            sb.AppendLine(price);
        }
        else
        {
            sb.AppendLine(CashierComponentLine(item, name, indent, context.Labels, parentQuantity));
        }

        AppendCashierChildren(sb, item, depth, context, requiredChoicesOnly: true);
        AppendDetailLines(sb, item, indent,
            new DetailLinesContext(context.Labels, item.Quantity, null, false));
        AppendCashierChildren(sb, item, depth, context, requiredChoicesOnly: false);
    }

    private static void AppendCashierChildren(
        StringBuilder sb, OrderItem item, int depth, CashierPrintContext context, bool requiredChoicesOnly)
    {
        var children = ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems)
            .Where(child => (child.CompositionRole == CompositionRole.RequiredChoice) == requiredChoicesOnly)
            .ToList();
        foreach (var child in children)
            AppendCashierProjected(sb, child, depth + 1, context, item.Quantity);
    }

    public static void AppendKitchenItemLines(
        StringBuilder sb, OrderItem item, int depth, PrintLabels labels, int parentQuantity = 1,
        PrintStyleSettings? styles = null)
    {
        if (depth == 0)
            item = ReceiptCompositionProjection.BuildSingle(item);
        AppendKitchenProjected(sb, item, depth, new KitchenPrintContext(labels, styles), parentQuantity);
    }

    private static void AppendKitchenProjected(
        StringBuilder sb, OrderItem item, int depth, KitchenPrintContext context, int parentQuantity)
    {
        var indent = new string(' ', depth * 3);
        var line = KitchenComponentLine(item, indent, context.Labels, parentQuantity);
        if (item.IsContextOnly)
            sb.AppendLine(line);
        else if ((depth == 0 && item.CompositionRole != CompositionRole.Dish)
            || item.QuantityBasis is null)
            ReceiptKitchenLineFormatter.AppendLegacyNameLine(sb, item, depth, indent, context.Labels, context.Styles);
        else
            ReceiptKitchenLineFormatter.AppendNameLine(sb, line, context.Styles);

        if (!string.IsNullOrWhiteSpace(item.VariationName))
            sb.AppendLine($"{indent}   - {item.VariationName}");

        AppendKitchenChildren(sb, item, depth, context, requiredChoicesOnly: true);
        AppendDetailLines(sb, item, indent,
            new DetailLinesContext(context.Labels, item.Quantity, context.Styles?.KitchenIngredients, true));
        AppendKitchenChildren(sb, item, depth, context, requiredChoicesOnly: false);
    }

    private static void AppendKitchenChildren(
        StringBuilder sb, OrderItem item, int depth, KitchenPrintContext context, bool requiredChoicesOnly)
    {
        var children = ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems)
            .Where(child => (child.CompositionRole == CompositionRole.RequiredChoice) == requiredChoicesOnly)
            .ToList();
        foreach (var child in children)
            AppendKitchenProjected(sb, child, depth + 1, context, item.Quantity);
    }

    private static string CashierComponentLine(
        OrderItem item, string name, string indent, PrintLabels labels, int parentQuantity)
    {
        var line = item.CompositionRole == CompositionRole.Dish
            && !string.IsNullOrWhiteSpace(item.PresentationLabel)
                ? ReceiptComponentFormatting.DishHeading(item, labels, parentQuantity)
                : string.Concat(
                    item.CompositionRole == CompositionRole.Extra ? $"{labels.SelectedPrefix} " : string.Empty,
                    item.Quantity,
                    "x ",
                    name,
                    ReceiptComponentFormatting.QuantityScopeSuffix(item, parentQuantity, labels));
        return item.IsContextOnly ? $"{indent}({line})" : $"{indent}{line}";
    }

    private static string KitchenComponentLine(
        OrderItem item, string indent, PrintLabels labels, int parentQuantity)
    {
        var line = item.CompositionRole == CompositionRole.Dish
            && !string.IsNullOrWhiteSpace(item.PresentationLabel)
                ? ReceiptComponentFormatting.DishHeading(item, labels, parentQuantity)
                : string.Concat(
                    item.CompositionRole == CompositionRole.Extra ? $"{labels.SelectedPrefix} " : string.Empty,
                    item.Quantity,
                    "x ",
                    item.ProductName,
                    ReceiptComponentFormatting.QuantityScopeSuffix(item, parentQuantity, labels));
        return item.IsContextOnly ? $"{indent}({line})" : $"{indent}{line}";
    }

    private static void AppendDetailLines(
        StringBuilder sb, OrderItem item, string indent, DetailLinesContext context)
    {
        foreach (var ingredient in (item.IngredientCustomizations ?? [])
            .Select((value, index) => new { Value = value, Index = index })
            .OrderBy(entry => entry.Value.PresentationOrder ?? int.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Value)
            .Where(ShouldPrintIngredient))
        {
            var line = FormatIngredientLine(ingredient, indent, context.Labels, context.ParentQuantity);
            ReceiptKitchenLineFormatter.AppendDetailLine(sb, line, context.Style, context.TallEmphasis);
        }
        var note = ReceiptItemDisplay.DisplaySpecialInstructions(item);
        if (note is not null)
            ReceiptKitchenLineFormatter.AppendDetailLine(sb,
                $"{indent}   {context.Labels.Note}: {note}", context.Style, context.TallEmphasis);
    }

    private static bool ShouldPrintIngredient(IngredientCustomization ingredient) =>
        ingredient.IsRemoved || ingredient.Quantity > 0;

    private static string FormatIngredientLine(
        IngredientCustomization ingredient, string indent, PrintLabels labels, int parentQuantity)
    {
        var prefix = $"{indent}   ";
        if (ingredient.IsRemoved)
            return $"{prefix}{labels.NoPrefix} {ingredient.IngredientName}";

        var marker = ReceiptComponentFormatting.QuantityScopeSuffix(ingredient, parentQuantity, labels);
        if (ingredient.IsAddOn || ingredient.Quantity > 1)
        {
            var count = ingredient.Quantity > 1 ? $"{ingredient.Quantity}x " : string.Empty;
            return $"{prefix}{labels.SelectedPrefix} {count}{ingredient.IngredientName}{marker}";
        }
        return $"{prefix}{labels.SelectedPrefix} {ingredient.IngredientName}{marker}";
    }
}
