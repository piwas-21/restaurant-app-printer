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
            if (!TryValidateRoute(route, targets, jobIds, out error))
                return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateRoute(
        OrderRoutingState? route,
        ISet<DevicePrintTarget> targets,
        ISet<Guid> jobIds,
        out string? error)
    {
        if (route is null)
            return Fail("The order contains a null printer route.", out error);
        if (route.JobId == Guid.Empty)
            return Fail($"The {route.Target} printer route has no job id.", out error);
        if (route.Revision <= 0)
            return Fail($"The {route.Target} printer route has an invalid revision.", out error);
        if (!Enum.IsDefined(route.Target))
            return Fail("The order contains an unknown printer route target.", out error);
        if (!Enum.IsDefined(route.Status))
            return Fail($"The {route.Target} printer route has an unknown status.", out error);
        if (!targets.Add(route.Target))
            return Fail($"The order contains duplicate {route.Target} printer routes.", out error);
        if (!jobIds.Add(route.JobId))
            return Fail($"The order contains duplicate printer job id {route.JobId}.", out error);
        if (route.Status == DevicePrintStatus.Queued && string.IsNullOrWhiteSpace(route.DeviceId))
            return Fail($"The queued {route.Target} printer route has no device id.", out error);

        error = null;
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
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
        if (selection.IsLegacy)
        {
            // Legacy responses have no server-owned route identity. Unknown kitchen outcomes are
            // still a deserialisation/routing failure, not proof that the order was handled.
            return front.Status != KitchenPrintStatus.Unknown
                && back.Status != KitchenPrintStatus.Unknown
                && general.Status != KitchenPrintStatus.Unknown;
        }

        if (!selection.IsValid || selection.EligibleTargets.Count == 0)
            return false;

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
