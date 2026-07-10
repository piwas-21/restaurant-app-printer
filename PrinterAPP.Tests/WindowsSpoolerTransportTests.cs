using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Cross-platform surface tests for <see cref="WindowsSpoolerTransport"/>. This test project
/// compiles without the WINDOWS symbol, so these pin the stub contract: argument validation is
/// platform-independent, and the transport fails loudly (never silently) off-Windows. The winspool
/// P/Invoke body only compiles in the Windows TFM and is exercised by the owner's Windows
/// on-device smoke, not here.
/// </summary>
public class WindowsSpoolerTransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_missing_printer_name(string? printerName)
    {
        Assert.Throws<ArgumentException>(() => new WindowsSpoolerTransport(printerName!));
    }

    [Fact]
    public void Implements_the_transport_seam()
    {
        Assert.IsAssignableFrom<IPrinterTransport>(new WindowsSpoolerTransport("EPSON TM-T20II"));
    }

    [Fact]
    public async Task SendAsync_validates_data_before_touching_the_platform()
    {
        var transport = new WindowsSpoolerTransport("EPSON TM-T20II");
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => transport.SendAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_honours_cancellation_before_sending()
    {
        var transport = new WindowsSpoolerTransport("EPSON TM-T20II");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync(new byte[] { 0x00 }, cts.Token));
    }

    [Fact]
    public async Task Non_windows_stub_throws_platform_not_supported()
    {
        var transport = new WindowsSpoolerTransport("EPSON TM-T20II");

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => transport.SendAsync(new byte[] { 0x00 }, CancellationToken.None));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => transport.TestAsync(CancellationToken.None));
    }

    [Fact]
    public void Spooler_document_name_is_the_legacy_one()
    {
        // The job name shown in the Windows print queue must not drift — operators recognise it.
        Assert.Equal("Restaurant Order", WindowsSpoolerTransport.DocumentName);
    }
}
