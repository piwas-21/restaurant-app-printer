using PrinterAPP.Models;

namespace PrinterAPP.Services;

internal static class CashierPaymentPresentation
{
    public static string State(Order order, PrintLabels labels)
    {
        if (string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
            return labels.Refunded;
        if (string.Equals(order.PaymentStatus, "Overpaid", StringComparison.OrdinalIgnoreCase))
            return labels.Overpaid;
        if (order.IsFullyPaid || (order.TotalPaid > 0 && order.RemainingAmount <= 0))
            return labels.Paid;
        return order.TotalPaid > 0 ? labels.PartiallyPaid : labels.Unpaid;
    }

    public static bool IsCaptured(Payment payment) => (payment.Status ?? string.Empty).Trim().ToUpperInvariant() is
        "COMPLETED" or "PARTIALLYREFUNDED" or "REFUNDED";
}
