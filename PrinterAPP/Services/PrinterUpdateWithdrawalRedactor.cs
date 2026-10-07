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
                Previous = RedactSnapshot(change.Previous),
                Current = RedactSnapshot(change.Current),
            })
            .ToArray(),
    };

    private static OrderItem? RedactSnapshot(OrderItem? item) =>
        item is null ? null : Redact(item);

    private static OrderItem Redact(OrderItem item)
    {
        var sideItems = item.SideItems?
            .Where(child => child is not null)
            .Select(Redact)
            .ToList();
        var copy = item.WithSideItems(sideItems);
        copy.SpecialInstructions = null;
        return copy;
    }
}
