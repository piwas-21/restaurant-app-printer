using System.Net;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// <see cref="PrinterEndpoint.TryParse"/> decides whether a configured printer target is a network
/// endpoint (IP literal) or a Windows spooler printer name. Getting this wrong would mis-route every
/// print, so the boundary cases are pinned here.
/// </summary>
public class PrinterEndpointTests
{
    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50", 9100)]      // bare IPv4 -> default port
    [InlineData("  10.0.0.5  ", "10.0.0.5", 9100)]          // trimmed
    [InlineData("192.168.1.50:9100", "192.168.1.50", 9100)] // explicit default port
    [InlineData("10.0.0.5:6001", "10.0.0.5", 6001)]         // custom port
    [InlineData("255.255.255.255:1", "255.255.255.255", 1)] // port lower bound
    public void TryParse_accepts_ip_targets(string value, string expectedIp, int expectedPort)
    {
        var ok = PrinterEndpoint.TryParse(value, out var ip, out var port);

        Assert.True(ok);
        Assert.Equal(IPAddress.Parse(expectedIp), ip);
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void TryParse_accepts_bare_ipv6_with_default_port()
    {
        var ok = PrinterEndpoint.TryParse("::1", out var ip, out var port);

        Assert.True(ok);
        Assert.Equal(IPAddress.IPv6Loopback, ip);
        Assert.Equal(9100, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EPSON TM-T20II")]            // Windows spooler printer name
    [InlineData("Microsoft Print to PDF")]    // ditto
    [InlineData("not.an.ip")]
    [InlineData("192.168.1.50:0")]            // port below range
    [InlineData("192.168.1.50:70000")]        // port above range
    [InlineData("192.168.1.50:abc")]          // non-numeric port
    [InlineData("192.168.1.50:")]             // empty port
    public void TryParse_rejects_non_ip_targets(string? value)
    {
        Assert.False(PrinterEndpoint.TryParse(value, out _, out _));
    }
}
