using System.Globalization;
using System.Text;

namespace PrinterAPP.Models;

/// <summary>Chooses the operator-facing table label from the stable order identity projection.</summary>
public static class TableDisplay
{
    /// <summary>
    /// Returns a nonblank display label when one is available, otherwise a positive legacy number.
    /// Stable IDs intentionally never become paper labels: they are relationship keys, not table
    /// names. The invariant format keeps diagnostics stable across device locales.
    /// </summary>
    public static string? ResolveLabel(string? tableLabel, int? tableNumber)
    {
        var sanitizedLabel = SanitizeLabel(tableLabel);
        if (sanitizedLabel is not null)
            return sanitizedLabel;

        return tableNumber is > 0
            ? tableNumber.Value.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Makes a server-provided table label safe for both diagnostic text and ESC/POS receipts.
    /// Control characters (including ESC and line separators) become one ordinary space, so a
    /// label can never inject printer commands or create a forged log line. A label made entirely
    /// of whitespace/control characters is treated as absent.
    /// </summary>
    public static string? SanitizeLabel(string? tableLabel)
    {
        if (string.IsNullOrWhiteSpace(tableLabel))
            return null;

        var builder = new StringBuilder(tableLabel.Length);
        foreach (var character in tableLabel)
        {
            var unsafeWhitespace = char.IsWhiteSpace(character) && character != ' ';
            if (char.IsControl(character) || unsafeWhitespace)
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                    builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    /// <summary>Formats an order type with the table only when the order has one.</summary>
    public static string FormatOrderType(string type, string? tableLabel, int? tableNumber, string tablePrefix) =>
        ResolveLabel(tableLabel, tableNumber) is { } label
            ? $"{type} - {tablePrefix} {label}"
            : type;
}
