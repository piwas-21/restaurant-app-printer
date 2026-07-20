using PrinterAPP.Services;

namespace PrinterAPP.Converters;

// Resolves a Sofra "craft" token (from Resources/Styles/Colors.xaml) to a Color for the CURRENT app
// theme. Converters + code-behind run in C#, so they can't use AppThemeBinding — they call this instead.
// Resolves on invocation, which is good enough for a utility where the theme rarely toggles mid-session
// (the same approach DiagnosticsPage already uses for its filter chips). Branding P2, refs #71.
public static class CraftColors
{
    public static Color Token(string lightKey, string darkKey)
    {
        var app = Application.Current;
        var key = app?.RequestedTheme == AppTheme.Dark ? darkKey : lightKey;
        return app?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Color color
            ? color
            : Colors.Gray;
    }

    public static Color Primary => Token("Primary", "PrimaryDark");
    public static Color Olive => Token("Secondary", "SecondaryDark");
    public static Color Accent => Token("Accent", "AccentDark");
    public static Color Success => Token("Success", "SuccessDark");
    public static Color Warning => Token("Warning", "WarningDark");
    public static Color Error => Token("Error", "ErrorDark");
    public static Color Muted => Token("TextMuted", "TextMutedDark");

    // Text-safe status colours: the moss/saffron fills are too light for small text on the cream card,
    // so status *labels* use the darker text-safe variant on light (the fill hue on dark reads fine).
    public static Color SuccessText => Token("SuccessText", "SuccessDark");
    public static Color WarningText => Token("WarningText", "WarningDark");

    // The craft re-map of the log-type accents (the palette has no blue/purple).
    public static Color ForLogType(LogType type) => type switch
    {
        LogType.SSE => Olive,
        LogType.Order => Primary,
        LogType.PrintRequest => Accent,
        LogType.PrintSuccess => Success,
        LogType.PrintError => Error,
        LogType.Error => Error,
        LogType.Warning => Warning,
        _ => Muted,
    };

    // Print-outcome semantics (both printed / one / none) → success / warning / error.
    public static Color ForPrintOutcome(string outcome) => outcome switch
    {
        "Printed" => Success,
        "Partial" => Warning,
        _ => Error,
    };
}
