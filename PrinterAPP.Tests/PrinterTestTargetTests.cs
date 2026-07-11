using System.Net;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// <see cref="PrinterTestTarget.Resolve"/> must encode the settings screen's historical routing
/// rules exactly (previously inlined in MainPage.OnTestPrintClicked): non-empty network entry wins,
/// an unparseable network entry is an input error (never a spooler fallback), the picker selection
/// is used otherwise, and nothing configured resolves to null.
/// </summary>
public class PrinterTestTargetTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Resolve_returns_null_when_nothing_configured(string? networkAddress, string? spoolerName)
    {
        Assert.Null(PrinterTestTarget.Resolve(networkAddress, spoolerName));
    }

    [Fact]
    public void Resolve_bare_ip_yields_valid_network_target_with_default_port()
    {
        var target = PrinterTestTarget.Resolve("192.168.1.50", null);

        Assert.NotNull(target);
        Assert.Equal(PrinterTransportKind.NetworkTcp, target.Kind);
        Assert.True(target.IsValid);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), target.Ip);
        Assert.Equal(NetworkTcpTransport.DefaultPort, target.Port);
        Assert.Null(target.PrinterName);
    }

    [Fact]
    public void Resolve_ip_with_port_yields_valid_network_target()
    {
        var target = PrinterTestTarget.Resolve(" 10.0.0.7:9101 ", "EPSON TM-T20II");

        Assert.NotNull(target);
        Assert.Equal(PrinterTransportKind.NetworkTcp, target.Kind);
        Assert.True(target.IsValid);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), target.Ip);
        Assert.Equal(9101, target.Port);
    }

    [Fact]
    public void Resolve_network_entry_takes_precedence_over_spooler_selection()
    {
        var target = PrinterTestTarget.Resolve("192.168.1.50", "EPSON TM-T20II (Default)");

        Assert.NotNull(target);
        Assert.Equal(PrinterTransportKind.NetworkTcp, target.Kind);
        Assert.Null(target.PrinterName);
    }

    [Fact]
    public void Resolve_unparseable_network_entry_is_invalid_not_a_spooler_fallback()
    {
        // Historical behaviour: a non-empty IP entry that fails to parse reports "Invalid IP" and
        // must NOT fall back to the selected spooler printer.
        var target = PrinterTestTarget.Resolve(" not-an-ip ", "EPSON TM-T20II (Default)");

        Assert.NotNull(target);
        Assert.Equal(PrinterTransportKind.NetworkTcp, target.Kind);
        Assert.False(target.IsValid);
        Assert.Equal("not-an-ip", target.RawText); // trimmed, for the error message
        Assert.Null(target.Ip);
        Assert.Null(target.PrinterName);
    }

    [Fact]
    public void Resolve_spooler_selection_used_when_network_entry_empty()
    {
        var target = PrinterTestTarget.Resolve("  ", "EPSON TM-T20II (Default)");

        Assert.NotNull(target);
        Assert.Equal(PrinterTransportKind.WindowsSpooler, target.Kind);
        Assert.True(target.IsValid);
        // Verbatim, including the " (Default)" suffix — the legacy print path strips it.
        Assert.Equal("EPSON TM-T20II (Default)", target.PrinterName);
        Assert.Null(target.Ip);
    }
}
