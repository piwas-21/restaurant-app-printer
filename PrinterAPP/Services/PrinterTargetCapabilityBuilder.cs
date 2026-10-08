using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Builds the complete non-secret target snapshot sent in a device heartbeat.</summary>
public static class PrinterTargetCapabilityBuilder
{
    public static List<PrinterTargetCapability> Build(PrinterConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var frontPrinter = FirstNonBlank(config.FrontKitchenPrinterName, config.CashierPrinterName);
        var backPrinter = FirstNonBlank(config.BackKitchenPrinterName, config.KitchenPrinterName);
        var generalPrinter = KitchenDestinationResolver.Resolve(new KitchenDestinationSettings(
            config.DefaultKitchenPrinterName,
            config.KitchenPrinterName,
            config.FrontKitchenPrinterName,
            config.BackKitchenPrinterName)).PrinterName;

        return new List<PrinterTargetCapability>
        {
            Capability(DevicePrintTarget.Cashier, config.CashierPrinterName, config.CashierAutoPrint),
            Capability(DevicePrintTarget.FrontKitchen, frontPrinter, config.FrontKitchenAutoPrint),
            Capability(DevicePrintTarget.BackKitchen, backPrinter, config.BackKitchenAutoPrint),
            Capability(DevicePrintTarget.General, generalPrinter, config.KitchenAutoPrint),
            Capability(DevicePrintTarget.Default, generalPrinter, config.KitchenAutoPrint),
        };
    }

    private static PrinterTargetCapability Capability(
        DevicePrintTarget target, string? printerName, bool autoPrintEnabled) =>
        new()
        {
            Target = target,
            IsSupported = true,
            IsConfigured = !string.IsNullOrWhiteSpace(printerName),
            AutoPrintEnabled = autoPrintEnabled,
            PrinterName = string.IsNullOrWhiteSpace(printerName) ? null : printerName.Trim(),
        };

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
