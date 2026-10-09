using System.Globalization;
using System.Text;
using PrinterAPP.Models;
// Disambiguate from Microsoft.Maui.FontSize under the MAUI TFMs (same alias PrintStyleSettingsService carries).
using FontSize = PrinterAPP.Models.FontSize;

namespace PrinterAPP.Services;

/// <summary>
/// The per-item block shared by both ticket surfaces, including recursive <see cref="OrderItem.SideItems"/> rendering.
/// Pure and MAUI-free: appends to the caller's <see cref="StringBuilder"/> so tests can pin text without a printer.
/// </summary>
public static class ReceiptComposer
{
    /// <summary>The wire value of backend OrderItemKind.SideItem (the API serialises enum NAMES).</summary>
    private const string SideItemKind = "SideItem";

    /// <summary>
    /// Money as printed, invariant to the machine: "31.90", never "31,90". A CHF receipt follows
    /// Swiss number style whatever locale the printer's Windows box runs in — a culture-sensitive
    /// F2 render put a comma on paper for fr-CH/de-DE hosts.
    /// </summary>
    public static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Money with its currency label (POS C18): "EUR 31.90" when the order declares a currency,
    /// a bare "31.90" when it does not — an unknown currency must never be invented into CHF,
    /// which is exactly how a EUR tenant's paper used to get CHF receipts.
    /// </summary>
    public static string Money(decimal amount, string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? Money(amount) : $"{currency} {Money(amount)}";

    /// <summary>
    /// A <c>SideItem</c> child is stored per parent unit and scales; a bundle child is line-absolute,
    /// while an unclassifiable row prints exactly as stored. This mirrors backend LineQuantity.
    /// </summary>
    private static int ChildQuantity(OrderItem child, int parentQuantity) =>
        string.Equals(child.Kind, SideItemKind, StringComparison.OrdinalIgnoreCase)
            ? child.Quantity * parentQuantity
            : child.Quantity;

    /// <summary>
    /// One line of the CASHIER receipt and everything hanging off it. Only the top-level line
    /// carries a price: child rows reach the order pinned at <c>ItemTotal = 0</c> (the parent's
    /// ItemTotal already covers its components and customization money — backend
    /// BasketToOrderTranslator/#54), so a price here would either print a meaningless CHF 0.00 or
    /// invite double-counting by hand.
    /// </summary>
    public static void AppendCashierItemLines(
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels, int parentQuantity = 1,
        string? currency = null)
    {
        var indent = new string(' ', depth * 3);
        var name = string.IsNullOrWhiteSpace(item.VariationName)
            ? item.ProductName
            : $"{item.ProductName} ({item.VariationName})";

        if (depth == 0)
        {
            var itemLine = $"{item.Quantity}x {name}";
            var price = Money(item.ItemTotal, currency);
            var dots = spacing - itemLine.Length - price.Length;
            sb.Append(itemLine);
            sb.Append(new string('.', Math.Max(1, dots)));
            sb.AppendLine(price);
        }
        else
        {
            // A component/add-on of the line above — no price of its own (see doc above), and the
            // quantity is the whole-line one, so "3 pizzas, a cola each" reads "+ 3x Cola".
            var qty = ChildQuantity(item, parentQuantity);
            sb.AppendLine($"{indent}+ {qty}x {name}");
        }

        AppendDetailLines(sb, item, indent, tallEmphasis: false, labels);

        foreach (var side in ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems))
        {
            AppendCashierItemLines(sb, side, depth + 1, spacing, labels, parentQuantity: item.Quantity);
        }
    }

    /// <summary>
    /// One line of the KITCHEN ticket and everything hanging off it. A context-only line (kept by
    /// <see cref="KitchenTicketFilter"/> to say what the components below it belong to) prints
    /// parenthesised at normal size so it does not read as a dish to prepare; a real line prints
    /// wide, its customizations and note tall so they stand out.
    /// </summary>
    /// <summary>
    /// Optional <paramref name="styles"/> wires the settings page to the paper: when given, the item
    /// name, its quantity prefix and the ingredient/detail lines each take their configured section
    /// (Kitchen Item Names / Item Quantities / Ingredients). When null — every existing unit-test
    /// caller — the bytes are exactly what the hardcoded composer emitted before styles existed.
    /// </summary>
    public static void AppendKitchenItemLines(
        StringBuilder sb, OrderItem item, int depth, PrintLabels labels, int parentQuantity = 1,
        PrintStyleSettings? styles = null)
    {
        var indent = new string(' ', depth * 3);
        var qty = depth == 0 ? item.Quantity : ChildQuantity(item, parentQuantity);

        if (item.IsContextOnly)
        {
            sb.AppendLine($"{indent}({qty}x {item.ProductName})");
        }
        else
        {
            AppendKitchenNameLine(sb, item, qty, depth, indent, styles);
        }

        if (!string.IsNullOrWhiteSpace(item.VariationName))
        {
            sb.AppendLine($"{indent}   - {item.VariationName}");
        }

        AppendDetailLines(sb, item, indent, tallEmphasis: true, labels, styles);

        foreach (var side in ReceiptItemDisplay.OrderItemsForDisplay(item.SideItems))
        {
            AppendKitchenItemLines(sb, side, depth + 1, labels, parentQuantity: item.Quantity, styles: styles);
        }
    }

    /// <summary>
    /// The item's quantity-and-name line. Identical or unwired styles remain one contiguous run;
    /// differing quantity/name styles become separate runs.
    /// </summary>
    private static void AppendKitchenNameLine(
        StringBuilder sb, OrderItem item, int qty, int depth, string indent, PrintStyleSettings? styles)
    {
        var nameStyle = styles?.KitchenItemName;
        var qtyStyle = styles?.KitchenItemQuantity;
        var body = depth == 0 ? $"{qty}x {item.ProductName}" : $"{indent}+ {qty}x {item.ProductName}";

        if (nameStyle is null)
        {
            sb.Append(EscPosCommands.SizeWide);
            sb.AppendLine(body);
            sb.Append(EscPosCommands.SizeNormal);
            return;
        }

        if (SameRender(qtyStyle, nameStyle) || qtyStyle is null)
        {
            sb.Append(ApplyStyleCommands(nameStyle));
            sb.AppendLine(body);
            sb.Append(ResetStyleCommands(nameStyle));
            return;
        }

        sb.Append(ApplyStyleCommands(qtyStyle));
        sb.Append($"{qty}x ");
        sb.Append(ResetStyleCommands(qtyStyle));
        sb.Append(ApplyStyleCommands(nameStyle));
        sb.AppendLine(depth == 0 ? item.ProductName : $"{indent}+ {item.ProductName}");
        sb.Append(ResetStyleCommands(nameStyle));
    }

    /// <summary>True when two section styles render identically (or both are unwired) — the qty
    /// prefix and the name can then share one styled run instead of two.</summary>
    private static bool SameRender(SectionStyle? a, SectionStyle? b) =>
        a is null && b is null
        || (a is not null && b is not null
            && a.Size == b.Size && a.IsBold == b.IsBold && a.IsEmphasized == b.IsEmphasized
            && a.Alignment == b.Alignment);

    /// <summary>
    /// The on/off command pair for one configured section, inline because the composer is static
    /// and receives resolved styles rather than a settings service. Alignment first, then size,
    /// then weight — the same order the settings page's own preview prints them.
    /// </summary>
    private static string ApplyStyleCommands(SectionStyle style)
    {
        var commands = style.Alignment switch
        {
            PrintAlignment.Center => EscPosCommands.AlignCenter,
            PrintAlignment.Right => EscPosCommands.AlignRight,
            _ => EscPosCommands.AlignLeft,
        };
        commands += style.Size switch
        {
            FontSize.Tall => EscPosCommands.SizeTall,
            FontSize.Wide => EscPosCommands.SizeWide,
            FontSize.Double => EscPosCommands.SizeDouble,
            FontSize.Large => EscPosCommands.SizeLarge,
            _ => EscPosCommands.SizeNormal,
        };
        if (style.IsBold)
        {
            commands += EscPosCommands.BoldOn;
        }
        if (style.IsEmphasized)
        {
            commands += EscPosCommands.EmphasizedOn;
        }
        return commands;
    }

    /// <summary>The closing half of <see cref="ApplyStyleCommands"/>: everything it turned on, off again.</summary>
    private static string ResetStyleCommands(SectionStyle style)
    {
        var commands = EscPosCommands.SizeNormal;
        if (style.IsBold)
        {
            commands += EscPosCommands.BoldOff;
        }
        if (style.IsEmphasized)
        {
            commands += EscPosCommands.EmphasizedOff;
        }
        return commands;
    }

    /// <summary>
    /// The "Type: Dine-in - Table 7" line, appended only with a table when one actually applies —
    /// takeaway/delivery orders have none, and a dangling "Table" with a blank number reads broken.
    /// Stable labels take precedence over numeric compatibility values.
    /// </summary>
    public static void AppendTypeAndTableLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        var orderType = TableDisplay.FormatOrderType(
            labels.OrderType(order.Type), order.TableLabel, order.TableNumber, labels.Table);
        sb.AppendLine($"{labels.Type}: {orderType}");
    }

    /// <summary>The order-level notes block; both surfaces print it above the items, never under them.</summary>
    public static void AppendOrderNotesLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            sb.AppendLine($"{labels.Notes}: {order.Notes}");
        }
    }

    /// <summary>Appends the frozen ingredient projection and the item's special instruction.</summary>
    private static void AppendDetailLines(StringBuilder sb, OrderItem item, string indent, bool tallEmphasis, PrintLabels labels, PrintStyleSettings? styles = null)
    {
        var ingredientStyle = styles?.KitchenIngredients;
        AppendIngredientLines(sb, item, indent, labels, ingredientStyle, tallEmphasis);

        var specialInstructions = ReceiptItemDisplay.DisplaySpecialInstructions(item);
        if (specialInstructions is not null)
        {
            // Keep the note with its parent, before the recursive child walk in both callers.
            AppendStyledLine(sb, $"{indent}   {labels.Note}: {specialInstructions}", ingredientStyle, tallEmphasis);
        }
    }

    private static void AppendIngredientLines(
        StringBuilder sb, OrderItem item, string indent, PrintLabels labels, SectionStyle? style, bool tallEmphasis)
    {
        var ingredients = item.IngredientCustomizations?.Where(ShouldPrintIngredient)
            ?? Enumerable.Empty<IngredientCustomization>();
        foreach (var ingredient in ingredients)
        {
            AppendStyledLine(sb, FormatIngredientLine(ingredient, indent, labels), style, tallEmphasis);
        }
    }

    private static bool ShouldPrintIngredient(IngredientCustomization ingredient) =>
        ingredient.IsRemoved || ingredient.Quantity > 0;

    private static string FormatIngredientLine(IngredientCustomization ingredient, string indent, PrintLabels labels)
    {
        if (ingredient.IsRemoved)
        {
            return $"{indent}   - {labels.NoPrefix} {ingredient.IngredientName}";
        }

        if (ingredient.IsAddOn || ingredient.Quantity > 1)
        {
            return $"{indent}   {labels.ExtraPrefix} {ingredient.IngredientName} x{ingredient.Quantity}";
        }

        return $"{indent}   {labels.SelectedPrefix} {ingredient.IngredientName}";
    }

    private static void AppendStyledLine(StringBuilder sb, string line, SectionStyle? style, bool tallEmphasis)
    {
        if (style is not null)
        {
            sb.Append(ApplyStyleCommands(style));
            sb.AppendLine(line);
            sb.Append(ResetStyleCommands(style));
            return;
        }

        if (tallEmphasis)
        {
            sb.Append(EscPosCommands.SizeTall);
        }

        sb.AppendLine(line);

        if (tallEmphasis)
        {
            sb.Append(EscPosCommands.SizeNormal);
        }
    }
}
