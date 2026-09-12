using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Resolves a logical update target to a configured kitchen printer.</summary>
public static class UpdateJobRouting
{
    /// <summary>
    /// General and Default are intentionally resolved through the same explicit destination chain:
    /// configured Default/General, legacy KitchenPrinter, then one sole station. Cashier and station
    /// targets are not silently reinterpreted as a note destination.
    /// </summary>
    public static KitchenDestinationResolution Resolve(
        DevicePrintTarget target,
        PrinterConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return target is DevicePrintTarget.General or DevicePrintTarget.Default
            ? KitchenDestinationResolver.Resolve(new KitchenDestinationSettings(
                config.DefaultKitchenPrinterName,
                config.KitchenPrinterName,
                config.FrontKitchenPrinterName,
                config.BackKitchenPrinterName))
            : new KitchenDestinationResolution(
                KitchenDestinationResolutionKind.NotConfigured, null);
    }

    /// <summary>True for the two logical destinations accepted by update jobs.</summary>
    public static bool IsUpdateTarget(DevicePrintTarget target) =>
        target is DevicePrintTarget.General or DevicePrintTarget.Default;
}
