namespace PrinterAPP.Services;

/// <summary>
/// Resolves the language a ticket is composed in — a pure function so tests can drive every arm
/// without a printer (same shape as <see cref="FeedWatchdogDecision"/>).
/// <para>
/// Two inputs: the venue's app-level setting (<c>PrinterConfiguration.PrintLanguage</c>, chosen on
/// the settings page) and, for the "follow the order" option, the guest's own
/// <see cref="PrinterAPP.Models.Order.PreferredLanguage"/>. The venue picks; the order never overrides
/// a fixed choice.
/// </para>
/// <para>
/// Scope note: only languages whose scripts the receipt codepage (PC857, ADR-002) can actually render
/// are offered — Latin-script European languages. The backend's ten locales include ar/ru/zh, which
/// PC857 cannot encode (and which would need codepage switching plus RTL/CJK shaping the print path
/// does not have); resolving to one of them would print garbage, so <see cref="Resolve"/> falls back
/// to English instead. Order DETAILS (product, variation and ingredient names) are translated on
/// the BACKEND: the poll sends this venue's PrintLanguage as the feed's language parameter, and the
/// feed resolves names into it from the catalog's per-language descriptions, falling back to the
/// frozen checkout name when no translation exists (backend PrinterFeedQuery.Language).
/// </para>
/// </summary>
public static class PrintLanguagePolicy
{
    /// <summary>The fallback language, and the setting every existing install keeps until changed.</summary>
    public const string English = "en";

    /// <summary>Setting value meaning "use each order's PreferredLanguage, English when it has none".</summary>
    public const string Auto = "auto";

    /// <summary>Label languages the receipt codepage can render, in picker order.</summary>
    public static readonly IReadOnlyList<string> Supported = ["en", "de", "fr", "it", "es", "nl", "tr"];

    /// <summary>Pickers show the native name; keys are the codes in <see cref="Supported"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["it"] = "Italiano",
        ["es"] = "Español",
        ["nl"] = "Nederlands",
        ["tr"] = "Türkçe",
    };

    /// <summary>
    /// The effective label language: <see cref="Auto"/> resolves the order's preference when it is a
    /// supported code; anything unknown (blank, unsupported, or a script the codepage cannot render)
    /// falls back to <see cref="English"/>. Never returns null or an unsupported code.
    /// </summary>
    public static string Resolve(string? configured, string? orderPreferredLanguage)
    {
        if (string.Equals(configured, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(orderPreferredLanguage) ?? English;
        }

        return Normalize(configured) ?? English;
    }

    /// <summary>
    /// Position of a language code within <see cref="Supported"/> (picker ordering), or -1 when it
    /// is not a supported code. Note <see cref="M:PrinterAPP.Services.PrintLanguagePolicy.Resolve"/>
    /// never returns a code this cannot find.
    /// </summary>
    public static int SupportedIndexOf(string? code)
    {
        var candidate = Normalize(code);
        if (candidate is null)
        {
            return -1;
        }

        for (var i = 0; i < Supported.Count; i++)
        {
            if (Supported[i] == candidate)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The value when given, lower-cased and validated; otherwise null.</summary>
    private static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var candidate = code.Trim().ToLowerInvariant();
        return Supported.Contains(candidate) ? candidate : null;
    }
}
