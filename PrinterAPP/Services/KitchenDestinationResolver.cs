namespace PrinterAPP.Services;

/// <summary>
/// Configuration values relevant to selecting a physical General/Default kitchen destination.
/// This is deliberately a small typed input, not the persisted MAUI configuration: policy tests
/// and future configuration migration can use it without starting the app.
/// </summary>
public sealed record KitchenDestinationSettings(
    string? DefaultKitchenPrinterName,
    string? KitchenPrinterName,
    string? FrontKitchenPrinterName,
    string? BackKitchenPrinterName);

/// <summary>Why a General/Default destination did or did not resolve.</summary>
public enum KitchenDestinationResolutionKind
{
    ConfiguredDefault,
    LegacyKitchenPrinter,
    SoleConfiguredStation,
    NotConfigured,
}

/// <summary>
/// Typed destination result. <see cref="KitchenDestinationResolutionKind.NotConfigured"/> is not
/// a successful no-op; callers must keep the work pending and ask for configuration.
/// </summary>
public sealed record KitchenDestinationResolution(
    KitchenDestinationResolutionKind Kind,
    string? PrinterName)
{
    public bool IsConfigured => Kind != KitchenDestinationResolutionKind.NotConfigured;
}

/// <summary>
/// Resolves the only safe physical destination for General/Default work. It never guesses between
/// multiple stations and never falls back to the customer receipt printer.
/// </summary>
public static class KitchenDestinationResolver
{
    public static KitchenDestinationResolution Resolve(KitchenDestinationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var configuredDefault = NonBlank(settings.DefaultKitchenPrinterName);
        if (configuredDefault is not null)
        {
            return new(KitchenDestinationResolutionKind.ConfiguredDefault, configuredDefault);
        }

        var legacyKitchen = NonBlank(settings.KitchenPrinterName);
        if (legacyKitchen is not null)
        {
            return new(KitchenDestinationResolutionKind.LegacyKitchenPrinter, legacyKitchen);
        }

        var stations = new[]
        {
            NonBlank(settings.FrontKitchenPrinterName),
            NonBlank(settings.BackKitchenPrinterName),
        }
        .Where(name => name is not null)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        return stations.Count == 1
            ? new(KitchenDestinationResolutionKind.SoleConfiguredStation, stations[0])
            : new(KitchenDestinationResolutionKind.NotConfigured, null);
    }

    private static string? NonBlank(string? printerName) =>
        string.IsNullOrWhiteSpace(printerName) ? null : printerName.Trim();
}
