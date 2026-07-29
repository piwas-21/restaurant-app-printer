namespace PrinterAPP.Services;

/// <summary>
/// Parses a printer-target string into a FILE SINK directory: the diagnostic target that captures
/// the exact ESC/POS bytes a print would have sent, instead of sending them to hardware.
/// </summary>
/// <remarks>
/// Exists because the thermal-specific half of the pipeline — the byte stream, the cut, the column
/// width — cannot be observed on an ordinary printer. On Windows a non-thermal printer silently
/// takes <c>WindowsPrinterService</c>'s HTML fallback, which proves the feed and the layout but
/// says nothing about the bytes; on Android there is no fallback at all. A sink target closes that
/// gap on both platforms with no hardware.
/// <para>
/// Syntax is <c>file:</c> optionally followed by a directory. Bare <c>file:</c> lands under the
/// system temp dir. The prefix cannot collide with the other two target kinds:
/// <see cref="PrinterEndpoint.TryParse"/> only accepts literal IPs, and no spooler name
/// begins with <c>file:</c>. (Windows printer names are NOT colon-free in general — port-style
/// names like <c>Ne04:</c> exist — so the guarantee this relies on is the prefix, not the colon.)
/// </para>
/// <para>
/// Plain BCL only — no MAUI — so the routing stays unit-testable off-device, the same reason
/// <see cref="PrinterEndpoint"/> and <see cref="PrinterTransportResolver"/> are shaped this way.
/// </para>
/// </remarks>
public static class PrinterFileSink
{
    /// <summary>The target prefix that selects a file sink.</summary>
    public const string Prefix = "file:";

    /// <summary>Directory used when the target is a bare <c>file:</c> with no path.</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "rumi-print-sink");

    /// <summary>
    /// Returns <c>true</c> and the resolved <paramref name="directory"/> when
    /// <paramref name="value"/> is a <c>file:</c> target. Case-insensitive, so a settings field
    /// typed as <c>File:</c> still routes here rather than falling through to the spooler and
    /// failing with an opaque "printer not found".
    /// </summary>
    public static bool TryParse(string? value, out string directory)
    {
        directory = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var path = trimmed[Prefix.Length..].Trim();
        directory = string.IsNullOrEmpty(path) ? DefaultDirectory : path;
        return true;
    }
}
