using System.Net;
using System.Net.Sockets;

namespace PrinterAPP.Services;

/// <summary>
/// Cross-platform <see cref="IPrinterTransport"/> that sends raw ESC/POS bytes to a network thermal
/// printer over TCP (the standard RAW / JetDirect port 9100). The default transport on every
/// platform; on Windows the legacy spooler transport remains available for USB/Bluetooth-attached
/// printers. Phase 2 of docs/plans/PRINTER-APP-CROSSPLATFORM-PLAN.md. See ADR-006.
/// </summary>
public sealed class NetworkTcpTransport : IPrinterTransport
{
    /// <summary>RAW / JetDirect port — the de-facto standard for ESC/POS over TCP.</summary>
    public const int DefaultPort = 9100;

    private readonly IPAddress _ip;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _writeTimeout;
    private readonly TimeSpan _retryDelay;

    /// <param name="ip">Printer IP address.</param>
    /// <param name="port">TCP port (default 9100).</param>
    /// <param name="connectTimeout">Per-attempt connect timeout (default 5s).</param>
    /// <param name="writeTimeout">Write timeout (default 10s).</param>
    /// <param name="retryDelay">Delay before the single connection-refused retry (default 250ms).</param>
    public NetworkTcpTransport(
        IPAddress ip,
        int port = DefaultPort,
        TimeSpan? connectTimeout = null,
        TimeSpan? writeTimeout = null,
        TimeSpan? retryDelay = null)
    {
        _ip = ip ?? throw new ArgumentNullException(nameof(ip));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1..65535.");
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(10);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var client = await ConnectWithRetryAsync(ct).ConfigureAwait(false);
        await using var stream = client.GetStream();

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeCts.CancelAfter(_writeTimeout);
        await stream.WriteAsync(data, writeCts.Token).ConfigureAwait(false);
        await stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
    }

    public async Task<bool> TestAsync(CancellationToken ct)
    {
        try
        {
            using var client = await ConnectWithRetryAsync(ct).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own connect timeout, not a caller cancellation
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task<TcpClient> ConnectWithRetryAsync(CancellationToken ct)
    {
        try
        {
            return await ConnectOnceAsync(ct).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // One retry on connection refused (printer momentarily busy / just power-cycled).
            // Host-unreachable / host-not-found and connect *timeouts* fall through and fail fast.
            await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
            return await ConnectOnceAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<TcpClient> ConnectOnceAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectTimeout);
            await client.ConnectAsync(_ip, _port, connectCts.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
