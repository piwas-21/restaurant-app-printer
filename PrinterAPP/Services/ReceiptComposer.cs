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
    /// <summary>The wire value of backend OrderItemKind.SideItem (the API serialises enum NAMES).</summary>
    private const string SideItemKind = "SideItem";

    /// <summary>
    /// Money as printed, invariant to the machine: "31.90", never "31,90". A CHF receipt follows
    /// Swiss number style whatever locale the printer's Windows box runs in — a culture-sensitive
    /// F2 render put a comma on paper for fr-CH/de-DE hosts.
    /// </summary>
    public static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// A child's quantity for the WHOLE line, mirroring backend OrderChildRendering.LineQuantity
    /// with the one signal the feed payload carries: an explicit <c>Kind == "SideItem"</c> is
    /// stored PER UNIT of its parent, so it scales by the parent line; a bundle child is already
    /// line-absolute, and an unclassifiable row (Kind null) prints exactly as stored — the backend
    /// itself refuses to scale what it cannot classify, because the alternative once measured a
    /// stored 6 rendering as 18 on a 3-unit line.
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
        StringBuilder sb, OrderItem item, int depth, int spacing, PrintLabels labels, int parentQuantity = 1)
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
            // A component/add-on of the line above — no price of its own (see doc above), and the
            // quantity is the whole-line one, so "3 pizzas, a cola each" reads "+ 3x Cola".
            var qty = ChildQuantity(item, parentQuantity);
            sb.AppendLine($"{indent}+ {qty}x {item.ProductName}");
        }

        AppendDetailLines(sb, item, indent, tallEmphasis: false, labels);

        foreach (var side in item.SideItems ?? Enumerable.Empty<OrderItem>())
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
        else if (SameRender(styles?.KitchenItemQuantity, styles?.KitchenItemName))
        {
            // The two sections agree (or styles are unwired): one styled line, exactly the bytes
            // the hardcoded composer emitted, so "2x Fries" stays one contiguous run of text.
            if (styles?.KitchenItemName is { } shared)
            {
                sb.Append(ApplyStyleCommands(shared));
                sb.AppendLine(depth == 0
                    ? $"{qty}x {item.ProductName}"
                    : $"{indent}+ {qty}x {item.ProductName}");
                sb.Append(ResetStyleCommands(shared));
            }
            else
            {
                sb.Append(EscPosCommands.SizeWide);
                sb.AppendLine(depth == 0
                    ? $"{qty}x {item.ProductName}"
                    : $"{indent}+ {qty}x {item.ProductName}");
                sb.Append(EscPosCommands.SizeNormal);
            }
        }
        else
        {
            // The venue styled quantity and name differently — two runs, which is the point.
            var qtyStyle = styles!.KitchenItemQuantity;
            var nameStyle = styles.KitchenItemName;
            sb.Append(ApplyStyleCommands(qtyStyle));
            sb.Append($"{qty}x ");
            sb.Append(ResetStyleCommands(qtyStyle));
            sb.Append(ApplyStyleCommands(nameStyle));
            sb.AppendLine(depth == 0 ? item.ProductName : $"{indent}+ {item.ProductName}");
            sb.Append(ResetStyleCommands(nameStyle));
        }

        if (!string.IsNullOrWhiteSpace(item.VariationName))
        {
            sb.AppendLine($"{indent}   - {item.VariationName}");
        }

        AppendDetailLines(sb, item, indent, tallEmphasis: true, labels, styles);

        foreach (var side in item.SideItems ?? Enumerable.Empty<OrderItem>())
        {
            AppendKitchenItemLines(sb, side, depth + 1, labels, parentQuantity: item.Quantity, styles: styles);
        }
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
    /// One implementation because the two surfaces used to carry twin copies of it.
    /// </summary>
    public static void AppendTypeAndTableLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        if (order.TableNumber.HasValue && order.TableNumber.Value > 0)
        {
            sb.AppendLine($"{labels.Type}: {labels.OrderType(order.Type)} - {labels.Table} {order.TableNumber}");
        }
        else
        {
            sb.AppendLine($"{labels.Type}: {labels.OrderType(order.Type)}");
        }
    }

    /// <summary>The order-level notes block; both surfaces print it above the items, never under them.</summary>
    public static void AppendOrderNotesLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            sb.AppendLine($"{labels.Notes}: {order.Notes}");
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
    private static void AppendDetailLines(StringBuilder sb, OrderItem item, string indent, bool tallEmphasis, PrintLabels labels, PrintStyleSettings? styles = null)
    {
        // With styles wired (live app) the configured Ingredients section drives each line — its
        // default is Tall with no weight, byte-for-byte what the pinned tall toggle printed. With
        // styles null (existing unit-test callers) the legacy tall toggle behaves exactly as before.
        var ingredientStyle = styles?.KitchenIngredients;
        Action openStyle = ingredientStyle is not null
            ? () => sb.Append(ApplyStyleCommands(ingredientStyle))
            : () => { if (tallEmphasis) sb.Append(EscPosCommands.SizeTall); };
        Action closeStyle = ingredientStyle is not null
            ? () => sb.Append(ResetStyleCommands(ingredientStyle))
            : () => { if (tallEmphasis) sb.Append(EscPosCommands.SizeNormal); };

        foreach (var ing in item.IngredientCustomizations ?? Enumerable.Empty<IngredientCustomization>())
        {
            string line;
            if (ing.IsRemoved)
            {
                line = $"{indent}   - {labels.NoPrefix} {ing.IngredientName}";
            }
            else if (ing.Quantity > 1)
            {
                line = $"{indent}   {labels.ExtraPrefix} {ing.IngredientName} x{ing.Quantity}";
            }
            else
            {
                line = $"{indent}   {labels.SelectedPrefix} {ing.IngredientName}";
            }

            openStyle();
            sb.AppendLine(line);
            closeStyle();
        }

        if (!string.IsNullOrWhiteSpace(item.SpecialInstructions))
        {
            // Detail lines land BEFORE the recursive walk into SideItems (both callers), so a
            // parent's note never reads as if it belonged to the last child printed.
            openStyle();
            sb.AppendLine($"{indent}   {labels.Note}: {item.SpecialInstructions}");
            closeStyle();
        }
    }
}
