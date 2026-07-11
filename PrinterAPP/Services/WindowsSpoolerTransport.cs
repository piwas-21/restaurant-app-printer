#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
#endif

namespace PrinterAPP.Services;

/// <summary>
/// <see cref="IPrinterTransport"/> over the legacy Windows print spooler (winspool.drv, RAW
/// datatype) for driver/USB/Bluetooth-attached printers addressed by spooler name. The P/Invoke
/// send sequence is moved verbatim from <c>WindowsPrinterService.SendTextToPrinter</c>; failures
/// throw (per the <see cref="IPrinterTransport"/> contract) instead of returning <c>false</c>.
/// Phase 2b of docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md; see ADR-006.
/// Constructed per printer name (a per-target value), so it is not a DI singleton — same pattern
/// as <see cref="NetworkTcpTransport"/>. The type compiles on every TFM so call sites stay
/// cross-platform; on non-Windows targets both methods throw
/// <see cref="PlatformNotSupportedException"/>.
/// </summary>
public sealed class WindowsSpoolerTransport : IPrinterTransport
{
    /// <summary>Spooler job name shown in the Windows print queue (kept from the legacy path).</summary>
    public const string DocumentName = "Restaurant Order";

    private readonly string _printerName;

    /// <param name="printerName">The Windows spooler printer name (as enumerated/selected).</param>
    public WindowsSpoolerTransport(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("Printer name is required.", nameof(printerName));
        _printerName = printerName;
    }

    public Task SendAsync(byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();
#if WINDOWS
        // The winspool calls are synchronous Win32 API; they were called synchronously from the
        // async print path before this refactor too, so this preserves the existing behaviour.
        WriteRaw(_printerName, data);
        return Task.CompletedTask;
#else
        throw new PlatformNotSupportedException(
            "The Windows print spooler transport is only available on Windows.");
#endif
    }

    public Task<bool> TestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
#if WINDOWS
        if (!OpenPrinter(_printerName, out IntPtr hPrinter, IntPtr.Zero))
            return Task.FromResult(false);
        ClosePrinter(hPrinter);
        return Task.FromResult(true);
#else
        throw new PlatformNotSupportedException(
            "The Windows print spooler transport is only available on Windows.");
#endif
    }

#if WINDOWS
    /// <summary>
    /// The OpenPrinter → StartDocPrinter → StartPagePrinter → WritePrinter → End*/ClosePrinter RAW
    /// sequence, moved from <c>WindowsPrinterService</c>. Cleanup ordering matches the legacy code
    /// (page/doc ended and the handle closed on every path, including failures).
    /// </summary>
    private static void WriteRaw(string printerName, byte[] bytes)
    {
        var docInfo = new DOC_INFO_1
        {
            pDocName = DocumentName,
            pDatatype = "RAW"
        };

        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenPrinter failed for '{printerName}'");
        try
        {
            if (!StartDocPrinter(hPrinter, 1, ref docInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"StartDocPrinter failed for '{printerName}'");
            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"StartPagePrinter failed for '{printerName}'");
                try
                {
                    if (!WritePrinter(hPrinter, bytes, bytes.Length, out int written))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"WritePrinter failed for '{printerName}'");
                    if (written != bytes.Length)
                        throw new IOException($"WritePrinter wrote {written} of {bytes.Length} bytes to '{printerName}'.");
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    #region winspool.drv P/Invoke (moved from WindowsPrinterService)

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool OpenPrinter(string printerName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBuf, int cbBuf, out int pcWritten);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pDocName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pDatatype;
    }

    #endregion
#endif
}
