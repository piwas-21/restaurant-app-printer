using System.Text.Json;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Validates immutable printer update envelopes before their cursor can advance.</summary>
public static class PrinterFeedUpdateValidation
{
    private static readonly string[] RequiredProperties =
    ["jobId", "revision", "jobType", "target", "orderId", "orderNumber", "audience", "createdAt"];

    public static string? Validate(JsonElement element, PrinterFeedUpdate update)
    {
        foreach (var property in RequiredProperties)
        {
            if (!TryGetPropertyIgnoreCase(element, property, out _))
                return $"Update {property} is required.";
        }

        return Validate(update);
    }

    public static string? Validate(PrinterFeedUpdate update)
    {
        if (update.JobId == Guid.Empty) return "Update jobId is required.";
        if (update.Revision <= 0) return "Update revision must be positive.";
        if (update.JobType != DevicePrintJobType.Update) return "Update jobType is not Update.";
        if (!UpdateJobRouting.IsUpdateTarget(update.Target))
            return "Update target is not a kitchen destination.";
        if (update.OrderId == Guid.Empty) return "Update orderId is required.";
        if (!string.Equals(update.Audience, "Kitchen", StringComparison.OrdinalIgnoreCase))
            return "Update audience is not Kitchen.";
        if (string.IsNullOrWhiteSpace(update.OrderNumber)) return "Update orderNumber is required.";

        var changes = update.Changes ?? Array.Empty<PrinterFeedChange>();
        if (string.IsNullOrWhiteSpace(update.Text) && changes.Count == 0)
            return "Update text or structured changes are required.";
        var changeError = ValidateChanges(changes);
        if (changeError is not null) return changeError;
        if (update.CreatedAt == default) return "Update createdAt is required.";
        return null;
    }

    private static string? ValidateChanges(IReadOnlyList<PrinterFeedChange> changes)
    {
        foreach (var change in changes)
        {
            if (change is null)
                return "Update contains a null change.";

            var validSnapshots = change.Kind switch
            {
                KitchenChangeKind.Add => change.Previous is null && IsValidSnapshot(change.Current),
                KitchenChangeKind.Void => IsValidSnapshot(change.Previous) && change.Current is null,
                KitchenChangeKind.Replace =>
                    IsValidSnapshot(change.Previous) && IsValidSnapshot(change.Current),
                KitchenChangeKind.InstructionChange => IsValidSnapshot(change.Previous)
                    && IsValidSnapshot(change.Current)
                    && HaveSameInstructionTarget(change.Previous, change.Current),
                _ => false,
            };
            if (!validSnapshots)
                return $"Update change {change.Kind} has invalid item snapshots.";
        }

        return null;
    }

    private static bool IsValidSnapshot(OrderItem? item) =>
        item is not null
        && !string.IsNullOrWhiteSpace(item.Id)
        && !string.IsNullOrWhiteSpace(item.ProductName)
        && item.Quantity > 0
        && (item.SideItems is null || item.SideItems.All(IsValidSnapshot));

    private static bool HaveSameInstructionTarget(OrderItem previous, OrderItem current) =>
        SameItemId(previous.Id, current.Id) && previous.Quantity == current.Quantity;

    private static bool SameItemId(string previousId, string currentId)
    {
        // Backend IDs are Guids. Compare parsed values when possible to mirror Guid equality,
        // while retaining exact matching for historic/synthetic non-Guid feed fixtures.
        if (Guid.TryParse(previousId, out var previousGuid)
            && Guid.TryParse(currentId, out var currentGuid))
        {
            return previousGuid == currentGuid;
        }

        return string.Equals(previousId, currentId, StringComparison.Ordinal);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject().Where(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
