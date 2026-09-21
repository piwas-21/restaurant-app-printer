using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Validates and selects server-owned order routes before any physical output.</summary>
public static class OrderRoutingStateValidation
{
    public static bool TryValidate(Order order, out string? error)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.RoutingStates is not { Count: > 0 })
        {
            error = null;
            return true;
        }

        var targets = new HashSet<DevicePrintTarget>();
        var jobIds = new HashSet<Guid>();
        foreach (var route in order.RoutingStates)
        {
            if (route is null)
            {
                error = "The order contains a null printer route.";
                return false;
            }

            if (route.JobId == Guid.Empty)
            {
                error = $"The {route.Target} printer route has no job id.";
                return false;
            }

            if (route.Revision <= 0)
            {
                error = $"The {route.Target} printer route has an invalid revision.";
                return false;
            }

            if (!Enum.IsDefined(route.Target))
            {
                error = "The order contains an unknown printer route target.";
                return false;
            }

            if (!Enum.IsDefined(route.Status))
            {
                error = $"The {route.Target} printer route has an unknown status.";
                return false;
            }

            if (!targets.Add(route.Target))
            {
                error = $"The order contains duplicate {route.Target} printer routes.";
                return false;
            }

            if (!jobIds.Add(route.JobId))
            {
                error = $"The order contains duplicate printer job id {route.JobId}.";
                return false;
            }

            if (route.Status == DevicePrintStatus.Queued
                && string.IsNullOrWhiteSpace(route.DeviceId))
            {
                error = $"The queued {route.Target} printer route has no device id.";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static RoutingSelection Select(Order order, string? deviceId)
    {
        if (order.RoutingStates is not { Count: > 0 })
            return RoutingSelection.Legacy;

        if (!TryValidate(order, out var error))
            return new RoutingSelection(false, false, new HashSet<DevicePrintTarget>(), error);

        if (string.IsNullOrWhiteSpace(deviceId))
            return new RoutingSelection(false, false, new HashSet<DevicePrintTarget>(),
                "The device identity is unavailable for a routed order.");

        var eligible = order.RoutingStates
            .Where(route => route.Status == DevicePrintStatus.Queued
                && string.Equals(route.DeviceId, deviceId, StringComparison.Ordinal))
            .Select(route => route.Target)
            .ToHashSet();
        return new RoutingSelection(false, true, eligible, null);
    }

    public static bool CanConfirm(
        Order order,
        string? deviceId,
        bool cashier,
        KitchenPrintOutcome front,
        KitchenPrintOutcome back,
        KitchenPrintOutcome general)
    {
        var selection = Select(order, deviceId);
        if (selection.IsLegacy || !selection.IsValid || selection.EligibleTargets.Count == 0)
            return selection.IsLegacy;

        foreach (var target in selection.EligibleTargets)
        {
            var outcome = target switch
            {
                DevicePrintTarget.Cashier => cashier ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                DevicePrintTarget.FrontKitchen => front,
                DevicePrintTarget.BackKitchen => back,
                DevicePrintTarget.General or DevicePrintTarget.Default => general,
                _ => KitchenPrintOutcome.Unknown,
            };
            if (!outcome.IsSuccess)
                return false;
        }

        return true;
    }

    public sealed record RoutingSelection(
        bool IsLegacy,
        bool IsValid,
        IReadOnlySet<DevicePrintTarget> EligibleTargets,
        string? FailureReason)
    {
        public static RoutingSelection Legacy { get; } = new(
            true, true, new HashSet<DevicePrintTarget>(), null);
    }
}
