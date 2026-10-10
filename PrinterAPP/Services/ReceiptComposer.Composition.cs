using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public static partial class ReceiptComposer
{
    public static void AppendCashierItemLines(
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels, int parentQuantity = 1,
        string? currency = null)
    {
        if (depth == 0)
            item = ReceiptCompositionProjection.BuildSingle(item);
        AppendCashierProjected(sb, item, depth, spacing, labels, parentQuantity, currency,
            item.Quantity, null);
    }

    private static void AppendCashierProjected(
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels,
        int parentQuantity, string? currency, int menuQuantity, string? ownerLabel)
    {
        var indent = new string(' ', depth * 3);
        var name = string.IsNullOrWhiteSpace(item.VariationName)
            ? item.ProductName : $"{item.ProductName} ({item.VariationName})";

        if (depth == 0)
        {
            var itemLine = $"{item.Quantity}x {name}";
            var price = Money(item.ItemTotal, currency);
            sb.Append(itemLine);
            sb.Append(new string('.', Math.Max(1, spacing - itemLine.Length - price.Length)));
            sb.AppendLine(price);
        }
        else
        {
            sb.AppendLine(CashierComponentLine(item, name, indent, labels, parentQuantity, menuQuantity, ownerLabel));
        }

        AppendCashierChildren(sb, item, depth, spacing, labels, currency, menuQuantity,
            ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName,
            requiredChoicesOnly: true);
        AppendDetailLines(sb, item, indent, labels, menuQuantity,
            ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName);
        AppendCashierChildren(sb, item, depth, spacing, labels, currency, menuQuantity,
            ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName,
            requiredChoicesOnly: false);
    }

    private static void AppendCashierChildren(
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels,
        string? currency, int menuQuantity, string ownerLabel, bool requiredChoicesOnly)
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
                    ReceiptComponentFormatting.LineTotalChoiceLabel(row, labels)));
                sb.AppendLine($"{new string(' ', (depth + 1) * 3)}{PrintLabelCatalog.TotalForMenusLabel(labels, menuQuantity)}: {choices}");
                foreach (var row in group)
                {
                    AppendCashierChildren(sb, row, depth + 1, spacing, labels, currency, menuQuantity,
                        ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName, requiredChoicesOnly: true);
                    AppendDetailLines(sb, row, new string(' ', (depth + 2) * 3), labels, menuQuantity,
                        ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName);
                    AppendCashierChildren(sb, row, depth + 1, spacing, labels, currency, menuQuantity,
                        ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName, requiredChoicesOnly: false);
                }
                continue;
            }
            AppendCashierProjected(sb, child, depth + 1, spacing, labels, item.Quantity, currency,
                menuQuantity, ownerLabel);
        }
    }

    public static void AppendKitchenItemLines(
        StringBuilder sb, OrderItem item, int depth, PrintLabels labels, int parentQuantity = 1,
        PrintStyleSettings? styles = null)
    {
        if (depth == 0)
            item = ReceiptCompositionProjection.BuildSingle(item);
        AppendKitchenProjected(sb, item, depth, labels, parentQuantity, styles, item.Quantity, null);
    }

    private static void AppendKitchenProjected(
        StringBuilder sb, OrderItem item, int depth, PrintLabels labels, int parentQuantity,
        PrintStyleSettings? styles, int menuQuantity, string? ownerLabel)
    {
        var indent = new string(' ', depth * 3);
        var line = KitchenComponentLine(item, indent, labels, parentQuantity, menuQuantity, ownerLabel);
        if (item.IsContextOnly)
            sb.AppendLine(line);
        else if ((depth == 0 && item.CompositionRole != CompositionRole.Dish)
            || item.QuantityBasis is null)
            ReceiptKitchenLineFormatter.AppendLegacyNameLine(sb, item, depth, indent, parentQuantity, styles);
        else
            ReceiptKitchenLineFormatter.AppendNameLine(sb, line, styles);

        if (!string.IsNullOrWhiteSpace(item.VariationName))
            sb.AppendLine($"{indent}   - {item.VariationName}");

        AppendKitchenChildren(sb, item, depth, labels, styles, menuQuantity,
            ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName,
            requiredChoicesOnly: true);
        AppendDetailLines(sb, item, indent, labels, menuQuantity,
            ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName,
            styles?.KitchenIngredients, tallEmphasis: true);

        AppendKitchenChildren(sb, item, depth, labels, styles, menuQuantity,
            ReceiptComponentFormatting.ComponentLabel(item) ?? item.ProductName,
            requiredChoicesOnly: false);
    }

    private static void AppendKitchenChildren(
        StringBuilder sb, OrderItem item, int depth, PrintLabels labels,
        PrintStyleSettings? styles, int menuQuantity, string ownerLabel, bool requiredChoicesOnly)
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
                    ReceiptComponentFormatting.LineTotalChoiceLabel(row, labels)));
                ReceiptKitchenLineFormatter.AppendNameLine(sb,
                    $"{new string(' ', (depth + 1) * 3)}{PrintLabelCatalog.TotalForMenusLabel(labels, menuQuantity)}: {choices}",
                    styles);
                foreach (var row in group)
                {
                    var indent = new string(' ', (depth + 2) * 3);
                    AppendKitchenChildren(sb, row, depth + 1, labels, styles, menuQuantity,
                        ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName, requiredChoicesOnly: true);
                    AppendDetailLines(sb, row, indent, labels, menuQuantity,
                        ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName,
                        styles?.KitchenIngredients, tallEmphasis: true);
                    AppendKitchenChildren(sb, row, depth + 1, labels, styles, menuQuantity,
                        ReceiptComponentFormatting.ComponentLabel(row) ?? row.ProductName, requiredChoicesOnly: false);
                }
                continue;
            }
            AppendKitchenProjected(sb, child, depth + 1, labels, item.Quantity, styles,
                menuQuantity, ownerLabel);
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
        StringBuilder sb, OrderItem item, string indent, PrintLabels labels,
        int menuQuantity, string ownerLabel, SectionStyle? style = null, bool tallEmphasis = false)
    {
        foreach (var ingredient in (item.IngredientCustomizations ?? [])
            .Select((value, index) => new { Value = value, Index = index })
            .OrderBy(entry => entry.Value.PresentationOrder ?? int.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Value)
            .Where(ShouldPrintIngredient))
        {
            var line = FormatIngredientLine(ingredient, indent, labels, menuQuantity, ownerLabel);
            ReceiptKitchenLineFormatter.AppendDetailLine(sb, line, style, tallEmphasis);
        }
        var note = ReceiptItemDisplay.DisplaySpecialInstructions(item);
        if (note is not null)
            ReceiptKitchenLineFormatter.AppendDetailLine(sb, $"{indent}   {labels.Note}: {note}", style, tallEmphasis);
    }

    private static bool ShouldPrintIngredient(IngredientCustomization ingredient) =>
        ingredient.IsRemoved || ingredient.Quantity > 0;

    private static string FormatIngredientLine(
        IngredientCustomization ingredient, string indent, PrintLabels labels,
        int menuQuantity, string ownerLabel)
    {
        var line = ingredient.IsRemoved
            ? $"{indent}   - {labels.NoPrefix} {ingredient.IngredientName}"
            : ingredient.IsAddOn || ingredient.Quantity > 1
                ? $"{indent}   {labels.ExtraPrefix} {ingredient.IngredientName} x{ingredient.Quantity}"
                : $"{indent}   {labels.SelectedPrefix} {ingredient.IngredientName}";
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
