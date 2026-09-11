namespace PrinterAPP.Services;

using PrinterAPP.Models;

/// <summary>One operator-entered kitchen target, either a network address or a Windows queue.</summary>
public sealed record KitchenRoutingTargetInput(
    string? NetworkAddress,
    string? SpoolerPrinterName)
{
    /// <summary>
    /// Gets the value persisted in <see cref="PrinterConfiguration"/>. Network entries win, just
    /// like the test-print controls, so an operator can leave a stale picker selection in place
    /// while switching a destination to Android/TCP.
    /// </summary>
    public string? ToStoredValue() => FirstNonBlank(NetworkAddress, SpoolerPrinterName);

    public static KitchenRoutingTargetInput FromStoredValue(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return new(null, null);
        }

        return PrinterTargetEntry.IsNetworkFieldTarget(normalized)
            ? new(normalized, null)
            : new(null, NormalizeSpoolerName(normalized));
    }

    private static string? FirstNonBlank(string? first, string? second) =>
        Normalize(first) ?? NormalizeSpoolerName(second);

    private static string? NormalizeSpoolerName(string? value) =>
        Normalize(value)?.Replace(" (Default)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// MAUI-free projection used by the settings page. The projection remembers when the displayed
/// General target came from the old KitchenPrinterName property so saving an unchanged legacy
/// config does not silently promote the fallback to an explicit DefaultKitchenPrinterName.
/// </summary>
public sealed record KitchenRoutingSettingsProjection(
    KitchenRoutingMode Mode,
    KitchenRoutingTargetInput GeneralTarget,
    KitchenRoutingTargetInput FrontTarget,
    KitchenRoutingTargetInput BackTarget,
    bool UsesLegacyGeneralFallback = false,
    string? LegacyGeneralTarget = null);

/// <summary>Friendly routing mode values for the MAUI Picker.</summary>
public sealed record KitchenRoutingModeOption(KitchenRoutingMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Validation result for the routing settings editor.</summary>
public sealed record KitchenRoutingSettingsValidation(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public string ErrorMessage => string.Join(Environment.NewLine, Errors);
}

/// <summary>
/// Maps persisted printer configuration to and from the settings screen without referencing MAUI.
/// Keeping this boundary pure makes legacy migration, validation, reset, and round-trip behavior
/// testable in the plain net10.0 test project.
/// </summary>
public static class KitchenRoutingSettingsMapper
{
    public static IReadOnlyList<KitchenRoutingModeOption> ModeOptions { get; } =
    [
        new(KitchenRoutingMode.SingleKitchen, "Single kitchen (all items)"),
        new(KitchenRoutingMode.Stations, "Stations (Front / Back)")
    ];

    public static KitchenRoutingSettingsProjection Load(PrinterConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var explicitDefault = NormalizeStoredTarget(config.DefaultKitchenPrinterName);
        var legacyDefault = NormalizeStoredTarget(config.KitchenPrinterName);
        var effectiveDefault = explicitDefault ?? legacyDefault;

        return new(
            NormalizeMode(config.KitchenRoutingMode),
            KitchenRoutingTargetInput.FromStoredValue(effectiveDefault),
            KitchenRoutingTargetInput.FromStoredValue(config.FrontKitchenPrinterName),
            KitchenRoutingTargetInput.FromStoredValue(config.BackKitchenPrinterName),
            UsesLegacyGeneralFallback: explicitDefault is null && legacyDefault is not null,
            LegacyGeneralTarget: legacyDefault);
    }

    /// <summary>
    /// Creates the edited projection while retaining the source metadata from the original load.
    /// If the operator leaves a legacy General target unchanged, the explicit field remains blank.
    /// </summary>
    public static KitchenRoutingSettingsProjection ForSave(
        KitchenRoutingSettingsProjection original,
        KitchenRoutingMode mode,
        KitchenRoutingTargetInput generalTarget,
        KitchenRoutingTargetInput frontTarget,
        KitchenRoutingTargetInput backTarget)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(generalTarget);
        ArgumentNullException.ThrowIfNull(frontTarget);
        ArgumentNullException.ThrowIfNull(backTarget);

        var generalValue = generalTarget.ToStoredValue();
        var keepsLegacyFallback = original.UsesLegacyGeneralFallback
            && string.Equals(generalValue, original.LegacyGeneralTarget, StringComparison.OrdinalIgnoreCase);

        return new(
            mode,
            generalTarget,
            frontTarget,
            backTarget,
            UsesLegacyGeneralFallback: keepsLegacyFallback,
            LegacyGeneralTarget: original.LegacyGeneralTarget);
    }

    /// <summary>
    /// Returns available Windows queues plus any saved routing queues that are currently
    /// disconnected. The UI can therefore round-trip configuration without clearing a target
    /// simply because EnumPrinters did not report it during this load.
    /// </summary>
    public static IReadOnlyList<string> WithSavedSpoolerTargets(
        IEnumerable<string> availablePrinters,
        KitchenRoutingSettingsProjection settings)
    {
        ArgumentNullException.ThrowIfNull(availablePrinters);
        ArgumentNullException.ThrowIfNull(settings);

        var choices = availablePrinters
            .Where(printer => !string.IsNullOrWhiteSpace(printer))
            .ToList();
        foreach (var target in new[]
                 { settings.GeneralTarget, settings.FrontTarget, settings.BackTarget })
        {
            AddMissingSpoolerChoice(choices, target.SpoolerPrinterName);
        }

        return choices;
    }

    public static KitchenRoutingSettingsValidation Validate(KitchenRoutingSettingsProjection settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        if (!IsKnownMode(settings.Mode))
        {
            errors.Add("Choose a valid kitchen routing mode.");
        }

        ValidateTarget(settings.GeneralTarget, "General / Default", errors);
        ValidateTarget(settings.FrontTarget, "Front kitchen", errors);
        ValidateTarget(settings.BackTarget, "Back kitchen", errors);

        return new(errors.Count == 0, errors);
    }

    /// <summary>Applies a validated editor projection and leaves legacy KitchenPrinterName untouched.</summary>
    public static void Apply(PrinterConfiguration config, KitchenRoutingSettingsProjection settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);

        var validation = Validate(settings);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage, nameof(settings));
        }

        config.KitchenRoutingMode = settings.Mode;
        config.DefaultKitchenPrinterName = settings.UsesLegacyGeneralFallback
            ? string.Empty
            : settings.GeneralTarget.ToStoredValue() ?? string.Empty;
        config.FrontKitchenPrinterName = settings.FrontTarget.ToStoredValue() ?? string.Empty;
        config.BackKitchenPrinterName = settings.BackTarget.ToStoredValue() ?? string.Empty;
    }

    public static KitchenRoutingMode NormalizeMode(KitchenRoutingMode mode) =>
        IsKnownMode(mode) ? mode : KitchenRoutingMode.Stations;

    public static KitchenRoutingModeOption OptionFor(KitchenRoutingMode mode) =>
        ModeOptions.First(option => option.Mode == NormalizeMode(mode));

    private static void ValidateTarget(
        KitchenRoutingTargetInput target,
        string label,
        ICollection<string> errors)
    {
        if (target is null)
        {
            errors.Add($"{label} target is missing.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.NetworkAddress)
            && !PrinterTargetEntry.IsNetworkFieldTarget(target.NetworkAddress))
        {
            errors.Add($"{label} network address is invalid. Use an IP[:port] or file: target.");
        }
    }

    private static void AddMissingSpoolerChoice(List<string> choices, string? savedPrinter)
    {
        if (string.IsNullOrWhiteSpace(savedPrinter))
            return;

        var normalized = savedPrinter.Trim();
        if (!choices.Any(printer => string.Equals(
                printer.Replace(" (Default)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
                normalized,
                StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(normalized);
        }
    }

    private static bool IsKnownMode(KitchenRoutingMode mode) =>
        mode is KitchenRoutingMode.SingleKitchen or KitchenRoutingMode.Stations;

    private static string? NormalizeStoredTarget(string? value) =>
        KitchenRoutingTargetInput.FromStoredValue(value).ToStoredValue();
}
