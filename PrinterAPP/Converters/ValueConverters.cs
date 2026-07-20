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

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Colors.Green : Colors.Red;
        }
        return Colors.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// LogType → accent Color for the Diagnostics log list. This UI mapping used to live on LogEntry as a
// `TypeColor` property, which coupled the (otherwise MAUI-free) model to MAUI's Color type and blocked
// source-linking it into the net10.0 E2E harness. It belongs here, with the other value converters.
public class LogTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Services.LogType type
            ? type switch
            {
                Services.LogType.SSE => Colors.Blue,
                Services.LogType.Order => Colors.Purple,
                Services.LogType.PrintRequest => Colors.Orange,
                Services.LogType.PrintSuccess => Colors.Green,
                Services.LogType.PrintError => Colors.Red,
                Services.LogType.Error => Colors.DarkRed,
                Services.LogType.Warning => Colors.DarkOrange,
                _ => Colors.Gray
            }
            : Colors.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
