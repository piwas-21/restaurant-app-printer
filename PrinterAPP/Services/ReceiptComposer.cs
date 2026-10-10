using System.Globalization;
using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Shared receipt lines and pure composition helpers, independent of MAUI and printer I/O.</summary>
public static partial class ReceiptComposer
{
    public static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    public static string Money(decimal amount, string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? Money(amount) : $"{currency} {Money(amount)}";

    public static void AppendTypeAndTableLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        var orderType = TableDisplay.FormatOrderType(
            labels.OrderType(order.Type), order.TableLabel, order.TableNumber, labels.Table);
        sb.AppendLine($"{labels.Type}: {orderType}");
    }

    public static void AppendOrderNotesLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        if (!string.IsNullOrWhiteSpace(order.Notes))
            sb.AppendLine($"{labels.Notes}: {order.Notes}");
    }
}
