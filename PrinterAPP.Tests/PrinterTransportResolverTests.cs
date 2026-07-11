using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Pins the order-print path's transport routing (<see cref="PrinterTransportResolver"/>, ADR-006
/// Phase 2b): the single configured printer-name field drives both network (IP literal) and
/// Windows spooler (any other name, used verbatim) — the same rule the legacy inline branch in
/// <c>OrderPrintService.PrintRawContentAsync</c> encoded. Endpoint parsing itself is pinned by
/// <see cref="PrinterEndpointTests"/>; the transports' own behaviour by
/// <see cref="NetworkTcpTransportTests"/> / <see cref="WindowsSpoolerTransportTests"/>.
/// </summary>
public class PrinterTransportResolverTests
{
    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("192.168.1.50:9101")]
    [InlineData("  10.0.0.7 ")]
    [InlineData("::1")]
    public void Ip_literal_resolves_to_the_network_transport(string printerName)
    {
        Assert.IsType<NetworkTcpTransport>(PrinterTransportResolver.Resolve(printerName));
    }

    [Theory]
    [InlineData("EPSON TM-T20II")]
    [InlineData("EPSON TM-T20II (Default)")] // verbatim, incl. suffix — matches the legacy order path
    [InlineData("Front Desk: Receipt")] // a colon does not make it an endpoint unless ip:port parses
    [InlineData("192.168.1.50:99999")] // out-of-range port -> not a valid endpoint -> spooler name
    public void Anything_else_resolves_to_the_spooler_transport(string printerName)
    {
        Assert.IsType<WindowsSpoolerTransport>(PrinterTransportResolver.Resolve(printerName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_name_fails_loudly_instead_of_reaching_the_wire(string printerName)
    {
        // Callers guard blank names before printing; if one slips through, the spooler transport's
        // constructor throws and OrderPrintService's catch maps it to the same print-failure result
        // (false) the legacy inline P/Invoke produced for an unopenable printer.
        Assert.Throws<ArgumentException>(() => PrinterTransportResolver.Resolve(printerName));
    }

    [Fact]
    public void Resolved_transports_implement_the_seam()
    {
        Assert.IsAssignableFrom<IPrinterTransport>(PrinterTransportResolver.Resolve("192.168.1.50"));
        Assert.IsAssignableFrom<IPrinterTransport>(PrinterTransportResolver.Resolve("EPSON TM-T20II"));
    }
}
