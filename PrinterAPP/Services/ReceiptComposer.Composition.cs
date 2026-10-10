using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public static partial class ReceiptComposer
{
    private sealed record CashierPrintContext(PrintLabels Labels, int Spacing, string? Currency, int MenuQuantity);

    private sealed record KitchenPrintContext(PrintLabels Labels, PrintStyleSettings? Styles, int MenuQuantity);

    private readonly record struct DetailLinesContext(
        PrintLabels Labels, int MenuQuantity, string OwnerLabel, SectionStyle? Style, bool TallEmphasis);

    public static void AppendCashierItemLines(
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels, int parentQuantity = 1,
        string? currency = null)
    {
        if (depth == 0)
            item = ReceiptCompositionProjection.BuildSingle(item);
        var context = new CashierPrintContext(labels, spacing, currency, item.Quantity);
        AppendCashierProjected(sb, item, depth, context, parentQuantity, null);
    }

    private static void AppendCashierProjected(
        StringBuilder sb, OrderItem item, int depth, CashierPrintContext context,
        int parentQuantity, string? ownerLabel)
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
            sb.AppendLine(CashierComponentLine(item, name, indent, context.Labels,
                parentQuantity, context.MenuQuantity, ownerLabel));
        }

        var componentLabel = ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName;
        AppendCashierChildren(sb, item, depth, context,
            componentLabel, requiredChoicesOnly: true);
        AppendDetailLines(sb, item, indent,
            new DetailLinesContext(context.Labels, context.MenuQuantity, componentLabel, null, false));
        AppendCashierChildren(sb, item, depth, context, componentLabel, requiredChoicesOnly: false);
    }

    private static void AppendCashierChildren(
        StringBuilder sb, OrderItem item, int depth, CashierPrintContext context,
        string ownerLabel, bool requiredChoicesOnly)
    {
        var children = ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems)
            .Where(child => (child.CompositionRole == CompositionRole.RequiredChoice) == requiredChoicesOnly)
            .ToList();
        var rendered = new HashSet<OrderItem>();
        foreach (var child in children)
        {
            if (!rendered.Add(child))
                continue;
            var group = ReceiptComponentFormatting.LineTotalChoiceGroup(children, child);
            if (group.Count > 1)
            {
                foreach (var row in group)
                    rendered.Add(row);
                var choices = string.Join("; ", group.Select(row =>
                    ReceiptComponentFormatting.LineTotalChoiceLabel(row, context.Labels)));
                sb.AppendLine($"{new string(' ', (depth + 1) * 3)}{PrintLabelCatalog.TotalForMenusLabel(context.Labels, context.MenuQuantity)}: {choices}");
                foreach (var row in group)
                {
                    var rowLabel = ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName;
                    AppendCashierChildren(sb, row, depth + 1, context, rowLabel, requiredChoicesOnly: true);
                    AppendDetailLines(sb, row, new string(' ', (depth + 2) * 3),
                        new DetailLinesContext(context.Labels, context.MenuQuantity, rowLabel, null, false));
                    AppendCashierChildren(sb, row, depth + 1, context, rowLabel, requiredChoicesOnly: false);
                }
                continue;
            }
            AppendCashierProjected(sb, child, depth + 1, context, item.Quantity, ownerLabel);
        }
    }

    public static void AppendKitchenItemLines(
        StringBuilder sb, OrderItem item, int depth, PrintLabels labels, int parentQuantity = 1,
        PrintStyleSettings? styles = null)
    {
        if (depth == 0)
            item = ReceiptCompositionProjection.BuildSingle(item);
        var context = new KitchenPrintContext(labels, styles, item.Quantity);
        AppendKitchenProjected(sb, item, depth, context, parentQuantity, null);
    }

    private static void AppendKitchenProjected(
        StringBuilder sb, OrderItem item, int depth, KitchenPrintContext context,
        int parentQuantity, string? ownerLabel)
    {
        var indent = new string(' ', depth * 3);
        var line = KitchenComponentLine(item, indent, context.Labels, parentQuantity, context.MenuQuantity, ownerLabel);
        if (item.IsContextOnly)
            sb.AppendLine(line);
        else if ((depth == 0 && item.CompositionRole != CompositionRole.Dish)
            || item.QuantityBasis is null)
            ReceiptKitchenLineFormatter.AppendLegacyNameLine(sb, item, depth, indent, parentQuantity, context.Styles);
        else
            ReceiptKitchenLineFormatter.AppendNameLine(sb, line, context.Styles);

        if (!string.IsNullOrWhiteSpace(item.VariationName))
            sb.AppendLine($"{indent}   - {item.VariationName}");

        var componentLabel = ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName;
        AppendKitchenChildren(sb, item, depth, context, componentLabel, requiredChoicesOnly: true);
        AppendDetailLines(sb, item, indent,
            new DetailLinesContext(context.Labels, context.MenuQuantity, componentLabel,
                context.Styles?.KitchenIngredients, TallEmphasis: true));
        AppendKitchenChildren(sb, item, depth, context, componentLabel, requiredChoicesOnly: false);
    }

    private static void AppendKitchenChildren(
        StringBuilder sb, OrderItem item, int depth, KitchenPrintContext context,
        string ownerLabel, bool requiredChoicesOnly)
    {
        var children = ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems)
            .Where(child => (child.CompositionRole == CompositionRole.RequiredChoice) == requiredChoicesOnly)
            .ToList();
        var rendered = new HashSet<OrderItem>();
        foreach (var child in children)
        {
            if (!rendered.Add(child))
                continue;
            var group = ReceiptComponentFormatting.LineTotalChoiceGroup(children, child);
            if (group.Count > 1)
            {
                foreach (var row in group)
                    rendered.Add(row);
                var choices = string.Join("; ", group.Select(row =>
                    ReceiptComponentFormatting.LineTotalChoiceLabel(row, context.Labels)));
                ReceiptKitchenLineFormatter.AppendNameLine(sb,
                    $"{new string(' ', (depth + 1) * 3)}{PrintLabelCatalog.TotalForMenusLabel(context.Labels, context.MenuQuantity)}: {choices}",
                    context.Styles);
                foreach (var row in group)
                {
                    var indent = new string(' ', (depth + 2) * 3);
                    var rowLabel = ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName;
                    AppendKitchenChildren(sb, row, depth + 1, context, rowLabel, requiredChoicesOnly: true);
                    AppendDetailLines(sb, row, indent,
                        new DetailLinesContext(context.Labels, context.MenuQuantity, rowLabel,
                            context.Styles?.KitchenIngredients, TallEmphasis: true));
                    AppendKitchenChildren(sb, row, depth + 1, context, rowLabel, requiredChoicesOnly: false);
                }
                continue;
            }
            AppendKitchenProjected(sb, child, depth + 1, context, item.Quantity, ownerLabel);
        }
    }

    private static string CashierComponentLine(
        OrderItem item, string name, string indent, PrintLabels labels,
        int parentQuantity, int menuQuantity, string? ownerLabel)
    {
        if (item.IsContextOnly)
            return $"{indent}({V2OrLegacyScope(item, labels, parentQuantity)})";
        if (item.CompositionRole == CompositionRole.Dish && !string.IsNullOrWhiteSpace(item.PresentationLabel))
            return $"{indent}{ReceiptComponentFormatting.DishHeading(item, labels, ownerLabel, menuQuantity)}";
        var prefix = item.QuantityBasis switch
        {
            QuantityBasis.PerParentUnit when ReceiptComponentFormatting.HasKnownPerParentScope(item.QuantityBasis, item.ConfigurationScope)
                => $"{PrintLabelCatalog.ForEachLabel(labels, ownerLabel ?? item.ProductName)} ",
            QuantityBasis.PerParentUnit => $"{labels.RecordedScopeUnknown}: ",
            QuantityBasis.LineTotal => $"{PrintLabelCatalog.TotalForMenusLabel(labels, menuQuantity)}: ",
            QuantityBasis.Unknown => $"{labels.RecordedScopeUnknown}: ",
            _ => $"{indent}+ {ReceiptComponentFormatting.DisplayQuantity(item, parentQuantity)}x ",
        };
        if (item.QuantityBasis is null)
            return $"{indent}+ {ReceiptComponentFormatting.DisplayQuantity(item, parentQuantity)}x {name}";
        var scopeNote = ReceiptComponentFormatting.ScopeNote(item.ConfigurationScope, labels);
        var rolePrefix = item.CompositionRole == CompositionRole.Extra ? $"{labels.ExtraPrefix} " : string.Empty;
        return $"{indent}{rolePrefix}{prefix}{name} ×{item.Quantity}{scopeNote}";
    }

    private static string KitchenComponentLine(
        OrderItem item, string indent, PrintLabels labels, int parentQuantity,
        int menuQuantity, string? ownerLabel)
    {
        if (item.IsContextOnly)
            return $"{indent}({V2OrLegacyScope(item, labels, parentQuantity)})";
        if (item.CompositionRole == CompositionRole.Dish && !string.IsNullOrWhiteSpace(item.PresentationLabel))
            return $"{indent}{ReceiptComponentFormatting.DishHeading(item, labels, ownerLabel, menuQuantity)}";
        if (item.QuantityBasis is null)
        {
            var quantity = ReceiptComponentFormatting.DisplayQuantity(item, parentQuantity);
            return indent.Length == 0 ? $"{quantity}x {item.ProductName}" : $"{indent}+ {quantity}x {item.ProductName}";
        }
        var prefix = item.QuantityBasis switch
        {
            QuantityBasis.PerParentUnit when ReceiptComponentFormatting.HasKnownPerParentScope(item.QuantityBasis, item.ConfigurationScope)
                => $"{PrintLabelCatalog.ForEachLabel(labels, ownerLabel ?? item.ProductName)} ",
            QuantityBasis.PerParentUnit => $"{labels.RecordedScopeUnknown}: ",
            QuantityBasis.LineTotal => $"{PrintLabelCatalog.TotalForMenusLabel(labels, menuQuantity)}: ",
            _ => $"{labels.RecordedScopeUnknown}: ",
        };
        var scopeNote = ReceiptComponentFormatting.ScopeNote(item.ConfigurationScope, labels);
        var rolePrefix = item.CompositionRole == CompositionRole.Extra ? $"{labels.ExtraPrefix} " : string.Empty;
        return $"{indent}{rolePrefix}{prefix}{item.ProductName} ×{item.Quantity}{scopeNote}";
    }

    private static string V2OrLegacyScope(OrderItem item, PrintLabels labels, int parentQuantity)
    {
        if (item.QuantityBasis is null)
            return $"{ReceiptComponentFormatting.DisplayQuantity(item, parentQuantity)}x {item.ProductName}";
        var scope = item.QuantityBasis is QuantityBasis.Unknown
            || (item.QuantityBasis == QuantityBasis.PerParentUnit
                && !ReceiptComponentFormatting.HasKnownPerParentScope(item.QuantityBasis, item.ConfigurationScope))
            ? $"{labels.RecordedScopeUnknown}: " : string.Empty;
        return $"{scope}{item.ProductName} ×{item.Quantity}{ReceiptComponentFormatting.ScopeNote(item.ConfigurationScope, labels)}";
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
            var line = FormatIngredientLine(ingredient, indent, context.Labels,
                context.MenuQuantity, context.OwnerLabel);
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
        IngredientCustomization ingredient, string indent, PrintLabels labels,
        int menuQuantity, string ownerLabel)
    {
        string line;
        if (ingredient.IsRemoved)
            line = $"{indent}   - {labels.NoPrefix} {ingredient.IngredientName}";
        else if (ingredient.IsAddOn || ingredient.Quantity > 1)
            line = $"{indent}   {labels.ExtraPrefix} {ingredient.IngredientName} x{ingredient.Quantity}";
        else
            line = $"{indent}   {labels.SelectedPrefix} {ingredient.IngredientName}";
        if (ingredient.QuantityBasis is QuantityBasis.Unknown
            || (ingredient.QuantityBasis == QuantityBasis.PerParentUnit
                && ingredient.ConfigurationScope != ConfigurationScope.SharedAcrossParentUnits))
        {
            var recordedQuantity = PrintLabelCatalog.RecordedQuantityLabel(labels, ingredient.Quantity);
            return $"{indent}   {labels.RecordedScopeUnknown}: {line.TrimStart()} [{recordedQuantity}]"
                + ReceiptComponentFormatting.ScopeNote(ingredient.ConfigurationScope, labels);
        }
        var scope = ingredient.QuantityBasis switch
        {
            QuantityBasis.PerParentUnit => $"{PrintLabelCatalog.ForEachLabel(labels, ownerLabel)} ",
            QuantityBasis.LineTotal => $"{PrintLabelCatalog.TotalForMenusLabel(labels, menuQuantity)}: ",
            _ => string.Empty,
        };
        return (scope.Length == 0 ? line : $"{indent}   {scope}{line.TrimStart()}")
            + ReceiptComponentFormatting.ScopeNote(ingredient.ConfigurationScope, labels);
    }

}
