using System.Globalization;
using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// The per-item block both ticket surfaces share — item line, ingredient customizations, special
/// instructions and the recursive walk into <see cref="OrderItem.SideItems"/> (bundle components
/// nest to arbitrary depth since backend PR #237, so both surfaces must recurse). Pure and
/// MAUI-free: appends to the caller's <see cref="StringBuilder"/> and decides nothing else, so the
/// unit tests can pin the composed text without a printer (same shape as
/// <see cref="KitchenTicketFilter"/>).
/// </summary>
public static class ReceiptComposer
{
    /// <summary>
    /// Money as printed, invariant to the machine: "31.90", never "31,90". A CHF receipt follows
    /// Swiss number style whatever locale the printer's Windows box runs in — a culture-sensitive
    /// F2 render put a comma on paper for fr-CH/de-DE hosts.
    /// </summary>
    public static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// One line of the CASHIER receipt and everything hanging off it. Only the top-level line
    /// carries a price: child rows reach the order pinned at <c>ItemTotal = 0</c> (the parent's
    /// ItemTotal already covers its components and customization money — backend
    /// BasketToOrderTranslator/#54), so a price here would either print a meaningless CHF 0.00 or
    /// invite double-counting by hand.
    /// </summary>
    public static void AppendCashierItemLines(StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels)
    {
        var indent = new string(' ', depth * 3);
        var name = string.IsNullOrWhiteSpace(item.VariationName)
            ? item.ProductName
            : $"{item.ProductName} ({item.VariationName})";

        if (depth == 0)
        {
            var itemLine = $"{item.Quantity}x {name}";
            var price = $"CHF {Money(item.ItemTotal)}";
            var dots = spacing - itemLine.Length - price.Length;
            sb.Append(itemLine);
            sb.Append(new string('.', Math.Max(1, dots)));
            sb.AppendLine(price);
        }
        else
        {
            // A component/add-on of the line above — no price of its own (see doc above).
            sb.AppendLine($"{indent}+ {item.Quantity}x {item.ProductName}");
        }

        AppendDetailLines(sb, item, indent, tallEmphasis: false, labels);

        foreach (var side in item.SideItems ?? Enumerable.Empty<OrderItem>())
        {
            AppendCashierItemLines(sb, side, depth + 1, spacing, labels);
        }
    }

    /// <summary>
    /// One line of the KITCHEN ticket and everything hanging off it. A context-only line (kept by
    /// <see cref="KitchenTicketFilter"/> to say what the components below it belong to) prints
    /// parenthesised at normal size so it does not read as a dish to prepare; a real line prints
    /// wide, its customizations and note tall so they stand out.
    /// </summary>
    public static void AppendKitchenItemLines(StringBuilder sb, OrderItem item, int depth, PrintLabels labels)
    {
        var indent = new string(' ', depth * 3);

        if (item.IsContextOnly)
        {
            sb.AppendLine($"{indent}({item.Quantity}x {item.ProductName})");
        }
        else
        {
            sb.Append(EscPosCommands.SizeWide);
            sb.AppendLine(depth == 0
                ? $"{item.Quantity}x {item.ProductName}"
                : $"{indent}+ {item.Quantity}x {item.ProductName}");
            sb.Append(EscPosCommands.SizeNormal);
        }

        if (!string.IsNullOrWhiteSpace(item.VariationName))
        {
            sb.AppendLine($"{indent}   - {item.VariationName}");
        }

        AppendDetailLines(sb, item, indent, tallEmphasis: true, labels);

        foreach (var side in item.SideItems ?? Enumerable.Empty<OrderItem>())
        {
            AppendKitchenItemLines(sb, side, depth + 1, labels);
        }
    }

    /// <summary>
    /// The ingredient rows a line carries, in the catalog's language, followed by its special
    /// instruction. Rows are exactly the projection the backend freezes at checkout — selected
    /// (kept), extra (quantity above one) and removed — because "selected ingredients don't appear
    /// on the paper" was the complaint: the old filter hid every selection at quantity one, which
    /// is precisely what an explicitly chosen sauce or topping is. A line with no customization
    /// rows prints nothing extra, so untouched recipes stay clean.
    /// </summary>
    private static void AppendDetailLines(StringBuilder sb, OrderItem item, string indent, bool tallEmphasis, PrintLabels labels)
    {
        foreach (var ing in item.IngredientCustomizations ?? Enumerable.Empty<IngredientCustomization>())
        {
            var line = ing.IsRemoved
                ? $"{indent}   - {labels.NoPrefix} {ing.IngredientName}"
                : ing.Quantity > 1
                    ? $"{indent}   {labels.ExtraPrefix} {ing.IngredientName} x{ing.Quantity}"
                    : $"{indent}   {labels.SelectedPrefix} {ing.IngredientName}";

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

        if (!string.IsNullOrWhiteSpace(item.SpecialInstructions))
        {
            // Detail lines land BEFORE the recursive walk into SideItems (both callers), so a
            // parent's note never reads as if it belonged to the last child printed.
            if (tallEmphasis)
            {
                sb.Append(EscPosCommands.SizeTall);
            }

            sb.AppendLine($"{indent}   {labels.Note}: {item.SpecialInstructions}");

            if (tallEmphasis)
            {
                sb.Append(EscPosCommands.SizeNormal);
            }
        }
    }
}
