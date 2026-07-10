using System.Net;

namespace PrinterAPP.Services;

/// <summary>
/// A resolved test-print target: a network printer (<see cref="PrinterTransportKind.NetworkTcp"/>)
/// or a Windows spooler printer (<see cref="PrinterTransportKind.WindowsSpooler"/>). Encodes the
/// settings screen's historical precedence rules so page code-behind stays UI-only (CLAUDE.md
/// §5.2): a non-empty network entry always wins (even when it fails to parse — that is an input
/// error, not a spooler fallback), the picker selection is used otherwise, and nothing configured
/// resolves to <c>null</c> (that printer is skipped).
/// </summary>
public sealed class PrinterTestTarget
{
    private PrinterTestTarget(PrinterTransportKind kind, bool isValid, IPAddress? ip, int port,
        string? printerName, string rawText)
    {
        Kind = kind;
        IsValid = isValid;
        Ip = ip;
        Port = port;
        PrinterName = printerName;
        RawText = rawText;
    }

    public PrinterTransportKind Kind { get; }

    /// <summary>False only when the network entry text was present but not a parseable IP[:port].</summary>
    public bool IsValid { get; }

    /// <summary>Resolved address for a valid <see cref="PrinterTransportKind.NetworkTcp"/> target.</summary>
    public IPAddress? Ip { get; }

    /// <summary>Resolved port for a valid <see cref="PrinterTransportKind.NetworkTcp"/> target.</summary>
    public int Port { get; }

    /// <summary>Spooler printer name (verbatim, incl. any " (Default)" suffix — the legacy print
    /// path strips it) for a <see cref="PrinterTransportKind.WindowsSpooler"/> target.</summary>
    public string? PrinterName { get; }

    /// <summary>The original (trimmed) user entry, for error messages.</summary>
    public string RawText { get; }

    /// <summary>
    /// Resolves the two settings-screen inputs for one printer into a target, or <c>null</c> when
    /// neither is configured. <paramref name="spoolerPrinterName"/> mirrors the picker's
    /// <c>SelectedItem?.ToString()</c> (null iff nothing is selected).
    /// </summary>
    public static PrinterTestTarget? Resolve(string? networkAddress, string? spoolerPrinterName)
    {
        if (!string.IsNullOrWhiteSpace(networkAddress))
        {
            var trimmed = networkAddress.Trim();
            if (PrinterEndpoint.TryParse(trimmed, out var ip, out var port))
                return new(PrinterTransportKind.NetworkTcp, isValid: true, ip, port, printerName: null, trimmed);
            return new(PrinterTransportKind.NetworkTcp, isValid: false, ip: null,
                NetworkTcpTransport.DefaultPort, printerName: null, trimmed);
        }

        if (spoolerPrinterName != null)
            return new(PrinterTransportKind.WindowsSpooler, isValid: true, ip: null,
                NetworkTcpTransport.DefaultPort, spoolerPrinterName, spoolerPrinterName);

        return null;
    }
}
