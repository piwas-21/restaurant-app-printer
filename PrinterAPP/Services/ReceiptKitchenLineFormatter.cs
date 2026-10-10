using System.Text;
using PrinterAPP.Models;
using FontSize = PrinterAPP.Models.FontSize;

namespace PrinterAPP.Services;

internal static class ReceiptKitchenLineFormatter
{
    public static void AppendDetailLine(StringBuilder sb, string line, SectionStyle? style, bool tallEmphasis)
    {
        if (style is not null)
        {
            sb.Append(Apply(style));
            sb.AppendLine(line);
            sb.Append(Reset(style));
            return;
        }
        if (tallEmphasis) sb.Append(EscPosCommands.SizeTall);
        sb.AppendLine(line);
        if (tallEmphasis) sb.Append(EscPosCommands.SizeNormal);
    }

    public static void AppendNameLine(StringBuilder sb, string line, PrintStyleSettings? styles)
    {
        var style = styles?.KitchenItemName;
        if (style is null)
        {
            sb.Append(EscPosCommands.SizeWide);
            sb.AppendLine(line);
            sb.Append(EscPosCommands.SizeNormal);
            return;
        }
        sb.Append(Apply(style));
        sb.AppendLine(line);
        sb.Append(Reset(style));
    }

    public static void AppendLegacyNameLine(
        StringBuilder sb, OrderItem item, int depth, string indent, PrintLabels labels,
        PrintStyleSettings? styles)
    {
        var quantity = item.Quantity;
        var actionPrefix = item.CompositionRole == CompositionRole.Extra
            ? $"{labels.SelectedPrefix} " : string.Empty;
        var body = depth == 0
            ? $"{quantity}x {item.ProductName}"
            : $"{indent}{actionPrefix}{quantity}x {item.ProductName}";
        var nameStyle = styles?.KitchenItemName;
        var quantityStyle = styles?.KitchenItemQuantity;
        if (nameStyle is null)
        {
            sb.Append(EscPosCommands.SizeWide);
            sb.AppendLine(body);
            sb.Append(EscPosCommands.SizeNormal);
        }
        else if (SameRender(quantityStyle, nameStyle) || quantityStyle is null)
        {
            sb.Append(Apply(nameStyle));
            sb.AppendLine(body);
            sb.Append(Reset(nameStyle));
        }
        else
        {
            if (depth > 0)
                sb.Append($"{indent}{actionPrefix}");
            sb.Append(Apply(quantityStyle));
            sb.Append($"{quantity}x ");
            sb.Append(Reset(quantityStyle));
            sb.Append(Apply(nameStyle));
            sb.AppendLine(item.ProductName);
            sb.Append(Reset(nameStyle));
        }
    }

    private static bool SameRender(SectionStyle? left, SectionStyle? right) =>
        left is null && right is null
        || (left is not null && right is not null
            && left.Size == right.Size && left.IsBold == right.IsBold
            && left.IsEmphasized == right.IsEmphasized && left.Alignment == right.Alignment);

    private static string Apply(SectionStyle style)
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
        if (style.IsBold) commands += EscPosCommands.BoldOn;
        if (style.IsEmphasized) commands += EscPosCommands.EmphasizedOn;
        return commands;
    }

    private static string Reset(SectionStyle style)
    {
        var commands = EscPosCommands.SizeNormal;
        if (style.IsBold) commands += EscPosCommands.BoldOff;
        if (style.IsEmphasized) commands += EscPosCommands.EmphasizedOff;
        return commands;
    }
}
