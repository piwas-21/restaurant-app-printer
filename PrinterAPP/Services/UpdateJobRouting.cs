using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Resolves a logical update target to a configured kitchen printer.</summary>
public static class UpdateJobRouting
{
    /// <summary>
    /// General and Default share the explicit destination chain. Front/Back update work follows the
    /// same station fallback as ordinary kitchen tickets. Cashier targets are never accepted.
    /// </summary>
    public static KitchenDestinationResolution Resolve(
        DevicePrintTarget target,
        PrinterConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return target switch
        {
            DevicePrintTarget.General or DevicePrintTarget.Default =>
                KitchenDestinationResolver.Resolve(new KitchenDestinationSettings(
                config.DefaultKitchenPrinterName,
                config.KitchenPrinterName,
                config.FrontKitchenPrinterName,
                config.BackKitchenPrinterName)),
            DevicePrintTarget.FrontKitchen => ResolveStation(
                config.FrontKitchenPrinterName, config.CashierPrinterName),
            DevicePrintTarget.BackKitchen => ResolveStation(
                config.BackKitchenPrinterName, config.KitchenPrinterName),
            _ => NotConfigured(),
        };
    }

    /// <summary>True for a logical kitchen destination accepted by update jobs.</summary>
    public static bool IsUpdateTarget(DevicePrintTarget target) =>
        target is DevicePrintTarget.General or DevicePrintTarget.Default
            or DevicePrintTarget.FrontKitchen or DevicePrintTarget.BackKitchen;

    public static bool AutoPrintEnabled(DevicePrintTarget target, PrinterConfiguration config) => target switch
    {
        DevicePrintTarget.FrontKitchen => config.FrontKitchenAutoPrint,
        DevicePrintTarget.BackKitchen => config.BackKitchenAutoPrint,
        DevicePrintTarget.General or DevicePrintTarget.Default => config.KitchenAutoPrint,
        _ => false,
    };

    private static KitchenDestinationResolution ResolveStation(string? stationPrinter, string? fallbackPrinter)
    {
        var printer = string.IsNullOrWhiteSpace(stationPrinter) ? fallbackPrinter : stationPrinter;
        return string.IsNullOrWhiteSpace(printer)
            ? NotConfigured()
            : new KitchenDestinationResolution(KitchenDestinationResolutionKind.ConfiguredStation, printer.Trim());
    }

    private static KitchenDestinationResolution NotConfigured() =>
        new(KitchenDestinationResolutionKind.NotConfigured, null);
}
