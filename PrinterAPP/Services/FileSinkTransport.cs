using System.Globalization;

namespace PrinterAPP.Services;

/// <summary>
/// Diagnostic <see cref="IPrinterTransport"/> that writes each print's raw ESC/POS bytes to a file
/// instead of sending them to hardware. Cross-platform (plain BCL, no MAUI).
/// </summary>
/// <remarks>
/// The point is to make the thermal-specific half of the pipeline observable without a thermal
/// printer. An ordinary printer cannot do that: on Windows a non-thermal name takes
/// <c>WindowsPrinterService</c>'s HTML fallback, which exercises the feed and the layout but never
/// the byte stream; on Android there is no fallback at all. Because
/// <see cref="PrinterTransportResolver"/> is what <c>OrderPrintService</c> uses, pointing a
/// configured printer at <c>file:</c> captures a REAL order print, not just a test receipt.
/// <para>
/// One file per send, never appended and never overwritten, so each capture is byte-exact and can
/// be diffed or fed to an ESC/POS decoder. Names sort chronologically.
/// </para>
/// <para>
/// ⚠️ Receipts carry customer data (delivery name and address). Captures are plain files that
/// nothing prunes, so this is a diagnostic to switch ON for a test and OFF afterwards — not a
/// standing configuration. The default directory is the system temp dir, which is app-private on
/// Android.
/// </para>
/// </remarks>
public sealed class FileSinkTransport : IPrinterTransport
{
    /// <summary>Extension for captured payloads — raw ESC/POS, not text.</summary>
    public const string CaptureExtension = ".escpos";

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;

    /// <param name="directory">Directory to write captures into; created on demand.</param>
    /// <param name="timeProvider">Clock for capture filenames. Injected so tests are deterministic.</param>
    public FileSinkTransport(string directory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Sink directory must not be blank.", nameof(directory));

        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The directory captures are written to.</summary>
    public string CaptureDirectory => _directory;

    /// <summary>
    /// Writes <paramref name="data"/> to a new capture file. Throws on failure, per
    /// <see cref="IPrinterTransport"/> — a sink that silently dropped a capture would be worse than
    /// no sink, since the whole point is to see the bytes.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);

        Directory.CreateDirectory(_directory);
        var path = NextCapturePath();

        // CreateNew, not Create: two prints inside the same clock tick must not have one silently
        // overwrite the other. NextCapturePath already uniquifies, but it checks then writes, so the
        // mode is what actually closes the race rather than merely narrowing it.
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes that the sink directory is writable, without leaving a capture behind. Never throws
    /// for the not-writable case, per <see cref="IPrinterTransport"/>; caller cancellation still
    /// propagates, matching <see cref="NetworkTcpTransport.TestAsync"/>. <c>async</c> so that
    /// cancellation FAULTS THE TASK rather than throwing out of the call itself — the two
    /// implementations of this interface must behave alike for a caller that stores the task.
    /// </summary>
    public async Task<bool> TestAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);

            // A real write, not just a directory check: a path can exist and still be read-only
            // (Android scoped storage, a locked-down box), which is exactly the case a "test
            // printer" button has to catch BEFORE an order depends on it.
            var probe = Path.Combine(_directory, $".probe-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(probe, [], ct).ConfigureAwait(false);
            File.Delete(probe);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A chronologically sortable capture path, uniquified if the clock has not advanced.
    /// </summary>
    private string NextCapturePath()
    {
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(_directory, $"{stamp}{CaptureExtension}");

        // Bounded rather than while(true): a clock stuck for 1000 captures is a broken clock, and
        // failing loudly beats spinning. FileMode.CreateNew turns the residual race into a throw.
        for (var suffix = 2; File.Exists(candidate) && suffix <= 1000; suffix++)
            candidate = Path.Combine(_directory, $"{stamp}-{suffix}{CaptureExtension}");

        return candidate;
    }
}
