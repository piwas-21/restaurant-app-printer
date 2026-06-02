using System.Net;

namespace PrinterAPP.Services;

/// <summary>
/// Parses a printer-target string into a network endpoint. A target is treated as a network printer
/// <em>iff</em> it is a literal IP address (optionally <c>"ip:port"</c>); anything else — e.g. a
/// Windows spooler printer name like <c>"EPSON TM-T20II"</c> — returns <c>false</c> and is left for
/// the spooler transport. Default port 9100 (RAW / JetDirect). This is how the same configuration
/// field drives both Windows (named spooler printer) and Android/network (IP) without a schema
/// change. Phase 2b of docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md.
/// </summary>
public static class PrinterEndpoint
{
    /// <summary>
    /// Returns <c>true</c> and the resolved <paramref name="ip"/>/<paramref name="port"/> when
    /// <paramref name="value"/> is a literal IP (IPv4/IPv6) or an <c>"ipv4:port"</c> pair.
    /// </summary>
    public static bool TryParse(string? value, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = NetworkTcpTransport.DefaultPort;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        // Whole string is a bare IP (IPv4 or IPv6) -> default port.
        if (IPAddress.TryParse(trimmed, out var whole))
        {
            ip = whole;
            return true;
        }

        // "ipv4:port" form (IPv6 with a port would need brackets; out of scope — printers are IPv4).
        var idx = trimmed.LastIndexOf(':');
        if (idx > 0 && idx < trimmed.Length - 1
            && IPAddress.TryParse(trimmed[..idx], out var hostIp)
            && int.TryParse(trimmed[(idx + 1)..], out var parsedPort)
            && parsedPort is >= 1 and <= 65535)
        {
            ip = hostIp;
            port = parsedPort;
            return true;
        }

        return false;
    }
}
