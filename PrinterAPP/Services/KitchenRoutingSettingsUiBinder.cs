using Microsoft.Maui.Controls;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>A picker plus its optional network-address entry for one kitchen destination.</summary>
public sealed record KitchenRoutingTargetControls(Picker PrinterPicker, Entry NetworkEntry);

/// <summary>All settings controls needed by the kitchen-routing binder.</summary>
public sealed record KitchenRoutingSettingsControls(
    Picker ModePicker,
    KitchenRoutingTargetControls General,
    KitchenRoutingTargetControls Front,
    KitchenRoutingTargetControls Back,
    Picker CashierPicker);

/// <summary>
/// Wires the MAUI controls for kitchen routing without putting projection or picker-selection
/// rules in MainPage. Decisions that do not require controls remain in
/// <see cref="KitchenRoutingSettingsMapper"/> and are covered by plain net10.0 tests.
/// </summary>
public static class KitchenRoutingSettingsUiBinder
{
    public static void ApplyToUi(
        KitchenRoutingSettingsControls controls,
        KitchenRoutingSettingsProjection settings)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(settings);

        controls.ModePicker.ItemsSource = KitchenRoutingSettingsMapper.ModeOptions.ToList();
        controls.ModePicker.SelectedItem = KitchenRoutingSettingsMapper.OptionFor(settings.Mode);
        SetTargetInput(controls.General, settings.GeneralTarget);
        SetTargetInput(controls.Front, settings.FrontTarget);
        SetTargetInput(controls.Back, settings.BackTarget);
    }

    public static KitchenRoutingSettingsProjection ReadFromUi(
        KitchenRoutingSettingsProjection original,
        KitchenRoutingSettingsControls controls)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(controls);

        var mode = controls.ModePicker.SelectedItem is KitchenRoutingModeOption option
            ? option.Mode
            : original.Mode;

        return KitchenRoutingSettingsMapper.ForSave(
            original,
            mode,
            ToInput(controls.General),
            ToInput(controls.Front),
            ToInput(controls.Back));
    }

    public static void PopulatePrinterPickers(
        IReadOnlyList<string> availablePrinters,
        KitchenRoutingSettingsProjection settings,
        KitchenRoutingSettingsControls controls,
        string? savedCashierPrinter)
    {
        ArgumentNullException.ThrowIfNull(availablePrinters);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(controls);

        var printerChoices = KitchenRoutingSettingsMapper.WithSavedSpoolerTargets(
            availablePrinters, settings).ToList();
        controls.General.PrinterPicker.ItemsSource = printerChoices;
        controls.Front.PrinterPicker.ItemsSource = printerChoices;
        controls.Back.PrinterPicker.ItemsSource = printerChoices;
        controls.CashierPicker.ItemsSource = printerChoices;
        SelectSpoolerPrinter(controls.General.PrinterPicker, settings.GeneralTarget.SpoolerPrinterName);
        SelectSpoolerPrinter(controls.Front.PrinterPicker, settings.FrontTarget.SpoolerPrinterName);
        SelectSpoolerPrinter(controls.Back.PrinterPicker, settings.BackTarget.SpoolerPrinterName);

        // Preserve the existing cashier convenience defaults. Kitchen targets intentionally do
        // not select an arbitrary queue because blank explicit targets have routing semantics.
        if (!string.IsNullOrEmpty(savedCashierPrinter))
        {
            var saved = printerChoices.FirstOrDefault(printer => printer.Contains(savedCashierPrinter));
            if (saved is not null)
            {
                controls.CashierPicker.SelectedItem = saved;
            }
            else
            {
                SelectCashierDefault(controls.CashierPicker, printerChoices);
            }
        }
        else
        {
            SelectCashierDefault(controls.CashierPicker, printerChoices);
        }
    }

    private static void SetTargetInput(
        KitchenRoutingTargetControls controls,
        KitchenRoutingTargetInput target)
    {
        controls.NetworkEntry.Text = target.NetworkAddress ?? string.Empty;
        controls.PrinterPicker.SelectedItem = null;
        SelectSpoolerPrinter(controls.PrinterPicker, target.SpoolerPrinterName);
    }

    private static KitchenRoutingTargetInput ToInput(KitchenRoutingTargetControls controls) =>
        new(controls.NetworkEntry.Text, controls.PrinterPicker.SelectedItem?.ToString());

    private static void SelectSpoolerPrinter(Picker picker, string? savedPrinter)
    {
        if (string.IsNullOrWhiteSpace(savedPrinter) || picker.ItemsSource is null)
            return;

        var normalized = savedPrinter.Trim();
        picker.SelectedItem = picker.ItemsSource
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(
                item.ToString()?.Replace(" (Default)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void SelectCashierDefault(Picker picker, IReadOnlyList<string> choices)
    {
        if (choices.Count == 0)
            return;

        picker.SelectedIndex = choices.Count > 1 ? 1 : 0;
    }
}
