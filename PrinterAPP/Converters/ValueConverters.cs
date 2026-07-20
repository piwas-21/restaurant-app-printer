using System.Globalization;

namespace PrinterAPP.Converters;

public class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string str && !string.IsNullOrWhiteSpace(str);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// true → success (moss), false → error (brick), from the craft palette, theme-aware. See CraftColors.
public class BoolToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool boolValue ? (boolValue ? CraftColors.Success : CraftColors.Error) : CraftColors.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// LogType → craft accent Color for the Diagnostics log list (the palette has no blue/purple, so the old
// raw Colors.Blue/Purple/… are re-mapped to craft tokens). See CraftColors.ForLogType.
public class LogTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Services.LogType type ? CraftColors.ForLogType(type) : CraftColors.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// Print-outcome semantic string ("Printed"/"Partial"/"None") → craft success/warning/error, theme-aware.
// Keeps colour presentation out of OrderHistoryItem (which now exposes the semantic PrintOutcome instead).
public class PrintOutcomeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string outcome ? CraftColors.ForPrintOutcome(outcome) : CraftColors.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
