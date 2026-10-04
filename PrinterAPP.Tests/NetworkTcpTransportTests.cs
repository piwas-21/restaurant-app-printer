using System.Net;
using System.Net.Sockets;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Golden-byte + behaviour tests for <see cref="NetworkTcpTransport"/> against an in-process
/// <see cref="TcpListener"/> on loopback — no printer hardware required. Asserts exact bytes/order,
/// the connection-refused retry, write/probe semantics, and constructor validation.
/// </summary>
public class NetworkTcpTransportTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Bind+release an ephemeral loopback port so connections to it are refused until reused.</summary>
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<byte[]> AcceptAndReadAllAsync(TcpListener listener, CancellationToken ct)
    {
        using var server = await listener.AcceptTcpClientAsync(ct);
        await using var stream = server.GetStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    [Fact]
    public async Task SendAsync_writes_exact_bytes_to_listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receivedTask = AcceptAndReadAllAsync(listener, cts.Token);

            // ESC/POS payload with PC857 (Turkish MS-DOS) bytes: ç=0x87, ğ=0xA6, ş=0x9F, plus
            // ESC @ init (1B 40) and GS V 0 full cut (1D 56 00). Asserting the raw bytes survive
            // unmodified is the whole point — the transport must not transcode or reorder.
            byte[] payload = { 0x1B, 0x40, (byte)'A', 0x87, 0xA6, 0x9F, 0x0A, 0x1D, 0x56, 0x00 };

            var transport = new NetworkTcpTransport(
                IPAddress.Loopback, port, connectTimeout: ShortTimeout, writeTimeout: ShortTimeout);
            await transport.SendAsync(payload, cts.Token);

            var received = await receivedTask;
            Assert.Equal(payload, received);
            Assert.True(transport.DeliveryMayHaveOccurred);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SendAsync_preserves_order_for_large_payload()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receivedTask = AcceptAndReadAllAsync(listener, cts.Token);

            var payload = new byte[8192];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);

            var transport = new NetworkTcpTransport(
                IPAddress.Loopback, port, connectTimeout: ShortTimeout, writeTimeout: ShortTimeout);
            await transport.SendAsync(payload, cts.Token);

            Assert.Equal(payload, await receivedTask);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SendAsync_retries_once_on_connection_refused_then_succeeds()
    {
        var port = GetFreePort(); // nothing listening yet -> first connect is refused
        byte[] payload = { 0x1B, 0x40, 0x42, 0x0A };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Bring the listener up shortly after the (refused) first attempt, inside the retry window.
        //
        // On a DEDICATED thread with a blocking sleep, not Task.Run + await Task.Delay: the rest of
        // this suite now finishes in a couple of seconds instead of idling through 5-second poll
        // delays, so the two-core CI runner's thread pool is genuinely busy while this test runs. A
        // pool-scheduled listener start can then land after the 800 ms retry has already been
        // refused a second time — observed as a real CI failure. A dedicated thread cannot be
        // starved, and nothing else here depends on the pool being free.
        var listenerUp = new TaskCompletionSource<TcpListener>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Thread(() =>
        {
            try
            {
                Thread.Sleep(150);
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listenerUp.SetResult(listener);
            }
            catch (Exception ex)
            {
                listenerUp.SetException(ex);
            }
        })
        { IsBackground = true };
        server.Start();

        var serverReceived = Task.Run(async () =>
        {
            var listener = await listenerUp.Task;
            try { return await AcceptAndReadAllAsync(listener, cts.Token); }
            finally { listener.Stop(); }
        }, cts.Token);

        var retryDelay = TimeSpan.FromMilliseconds(800); // > the 150ms listener-start delay
        var transport = new NetworkTcpTransport(
            IPAddress.Loopback, port,
            connectTimeout: ShortTimeout, writeTimeout: ShortTimeout,
            retryDelay: retryDelay);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await transport.SendAsync(payload, cts.Token); // succeeds on the retry
        elapsed.Stop();

        Assert.Equal(payload, await serverReceived);

        // The retry path is what this test exists for, and until now nothing pinned that it ran: if
        // the listener ever came up before the FIRST connect, the send would simply succeed and the
        // test would pass green while covering nothing. Only the retry can take longer than the
        // retry delay.
        Assert.True(
            elapsed.Elapsed >= retryDelay - TimeSpan.FromMilliseconds(100),
            $"the send completed in {elapsed.ElapsedMilliseconds}ms, faster than the {retryDelay.TotalMilliseconds}ms "
            + "retry delay — the first connect was not refused, so the retry was never exercised");
    }

    [Fact]
    public async Task SendAsync_throws_when_nothing_listening()
    {
        var port = GetFreePort();
        var transport = new NetworkTcpTransport(
            IPAddress.Loopback, port,
            connectTimeout: ShortTimeout, retryDelay: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.SendAsync(new byte[] { 0x1B, 0x40 }, CancellationToken.None));
        Assert.False(transport.DeliveryMayHaveOccurred);
    }

    [Fact]
    public async Task TestAsync_true_when_listener_present()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = Task.Run(async () =>
            {
                try { using var s = await listener.AcceptTcpClientAsync(cts.Token); }
                catch { /* listener stopped */ }
            }, cts.Token);

            var transport = new NetworkTcpTransport(IPAddress.Loopback, port, connectTimeout: ShortTimeout);
            Assert.True(await transport.TestAsync(cts.Token));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task TestAsync_false_when_nothing_listening()
    {
        var port = GetFreePort();
        var transport = new NetworkTcpTransport(
            IPAddress.Loopback, port,
            connectTimeout: ShortTimeout, retryDelay: TimeSpan.FromMilliseconds(20));

        Assert.False(await transport.TestAsync(CancellationToken.None));
    }

    [Fact]
    public void Constructor_validates_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new NetworkTcpTransport(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkTcpTransport(IPAddress.Loopback, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkTcpTransport(IPAddress.Loopback, 70000));
        // Non-positive connect/write timeouts and a negative retry delay are rejected.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetworkTcpTransport(IPAddress.Loopback, 9100, connectTimeout: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetworkTcpTransport(IPAddress.Loopback, 9100, writeTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetworkTcpTransport(IPAddress.Loopback, 9100, retryDelay: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void DefaultPort_is_9100()
    {
        Assert.Equal(9100, NetworkTcpTransport.DefaultPort);
    }
}
