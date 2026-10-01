using System.Globalization;
using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Pure source-aware print policy/composition; keeps ordinary receipt bytes unchanged.</summary>
public static class MarketplaceReceiptComposer
{
    public static string? Currency(Order order)
    {
        if (order.ExternalOrder is null) return order.Currency;
        var currency = order.ExternalOrder.Currency;
        return currency.Length == 3 && currency.All(char.IsAsciiLetterUpper) ? currency : null;
    }

    public static bool CanPrint(Order order, PrinterType type)
    {
        if (order.ExternalOrder is null) return true;
        if (type == PrinterType.Kitchen && !order.IsKitchenReleased) return false;
        var action = type == PrinterType.Kitchen ? "PrintKitchen" : "PrintReceipt";
        return order.PermittedActions?.Any(entry => entry.Action == action && entry.Allowed) == true;
    }

    public static string ProviderName(ExternalOrder source) =>
        source.Provider == "uber-eats" ? "Uber Eats" : SafeField(source.Provider);

    public static void AppendIdentity(StringBuilder builder, Order order, string language, bool showPayment)
    {
        var source = order.ExternalOrder;
        if (source is null) return;
        var labels = MarketplacePrintLabels.For(language);
        builder.AppendLine($"{ProviderName(source)} {SafeField(source.ExternalDisplayId)}");
        if (source.IsSandbox) builder.AppendLine(labels.TestOrder);
        if (showPayment) builder.AppendLine(string.Format(CultureInfo.InvariantCulture, labels.PaymentHandledBy, ProviderName(source)));
    }

    public static void AppendTax(StringBuilder builder, Order order, PrintLabels labels, string language)
    {
        if (order.ExternalOrder is { } source)
        {
            var tax = source.ReportedTax is { } amount
                ? ReceiptComposer.Money(amount, Currency(order))
                : MarketplacePrintLabels.For(language).TaxNotReported;
            builder.AppendLine($"{labels.Tax}: {tax}");
        }
        else if (order.Tax > 0)
            builder.AppendLine($"{labels.Tax}: {ReceiptComposer.Money(order.Tax, order.Currency)}");
    }

    // Display references are text, never ESC/POS commands or extra header lines.
    private static string SafeField(string value) => new(value.Where(character => !char.IsControl(character)).ToArray());
}
