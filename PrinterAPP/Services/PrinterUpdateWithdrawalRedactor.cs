using PrinterAPP.Models;

namespace PrinterAPP.Services;

public static class PrinterUpdateWithdrawalRedactor
{
    public static PrinterFeedUpdate Redact(PrinterFeedUpdate update) => update with
    {
        IsWithdrawn = true,
        Text = string.Empty,
        Changes = (update.Changes ?? Array.Empty<PrinterFeedChange>())
            .Select(change => change with
            {
                Previous = Redact(change.Previous),
                Current = Redact(change.Current),
            })
            .ToArray(),
    };

    private static OrderItem? Redact(OrderItem? item)
    {
        if (item is null)
            return null;

        var copy = item.WithSideItems(item.SideItems?.Select(child => Redact(child)!).ToList());
        copy.SpecialInstructions = null;
        return copy;
    }
}
