using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.E2E.Support;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.E2E;

/// <summary>
/// Fleet-telemetry E2E: the REAL <see cref="TelemetryClient"/> + <see cref="PrintAckOutbox"/> against a
/// REAL backend (no mocks of our code). Client 2xx == the backend validated + ingested it; when an admin
/// JWT is configured, we also assert the device shows in GET /api/devices. See docs/E2E-STRATEGY.md.
/// </summary>
public class FleetTelemetryE2ETests
{
    private static PrinterConfiguration TestConfig() => new()
    {
        ApiBaseUrl = E2EConfig.ApiBaseUrl,
        ApiKey = E2EConfig.ApiKey,
        TenantSlug = E2EConfig.TenantSlug,
        DeviceLabel = "E2E device",
        CashierPrinterName = "127.0.0.1:9100",
    };

    // One shared HttpClient (TelemetryClient sets headers per-request on the HttpRequestMessage, never
    // on DefaultRequestHeaders, so sharing is safe) — avoids socket churn across tests.
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static TelemetryClient NewClient() => new(SharedHttp, NullLogger<TelemetryClient>.Instance);

    // 🔴 HIGH — a real heartbeat registers the device + its feed state.
    [SkippableFact]
    public async Task Heartbeat_RealClient_IsIngestedByBackend()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");

        var deviceId = E2EConfig.NewDeviceId();
        var config = TestConfig();
        var request = TelemetryPayloads.Heartbeat(config, "Android", "e2e", feedRunning: true, DateTime.UtcNow);

        var accepted = await NewClient().SendHeartbeatAsync(request, config.ApiBaseUrl, config.ApiKey, deviceId);

        Assert.True(accepted, "backend rejected the heartbeat");

        // Stronger assertion when an admin JWT is available: the device appears in the fleet read.
        var devices = await Backend.DevicesJsonOrNullAsync();
        if (devices is not null)
            Assert.Contains(deviceId, devices);
    }

    // 🔴 HIGH — a real print-ack is accepted (the ingestion side of missed-order reconciliation).
    [SkippableFact]
    public async Task PrintAck_RealClient_IsIngestedByBackend()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");

        var acks = new List<PrintAck>
        {
            new()
            {
                OrderId = Guid.NewGuid(), // OrderId is a plain Guid by design — no real order needed to prove ingestion
                Target = DevicePrintTarget.Cashier,
                Status = DevicePrintStatus.Printed,
                ReceivedAt = DateTime.UtcNow,
                PrintedAt = DateTime.UtcNow,
                Copies = 1,
            },
        };

        var result = await NewClient().SendPrintAcksAsync(
            acks, E2EConfig.ApiBaseUrl, E2EConfig.ApiKey, E2EConfig.NewDeviceId());

        Assert.Equal(TelemetrySendResult.Sent, result);
    }

    // 🟡 MED — the durable outbox survives a "restart" and flushes to the real backend, then drains.
    [SkippableFact]
    public async Task Outbox_SurvivesRestart_FlushesToRealBackend_AndDrains()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");

        var deviceId = E2EConfig.NewDeviceId();
        using var paths = new TempPaths();
        var client = NewClient();

        var enqueuer = new PrintAckOutbox(paths, NullLogger<PrintAckOutbox>.Instance);
        await enqueuer.EnqueueAsync(new[]
        {
            new PrintAck
            {
                OrderId = Guid.NewGuid(),
                Target = DevicePrintTarget.FrontKitchen,
                Status = DevicePrintStatus.Printed,
                ReceivedAt = DateTime.UtcNow,
                PrintedAt = DateTime.UtcNow,
                Copies = 1,
            },
        });

        // Fresh instance over the same dir = a restart; the ack must still be there to flush.
        var reopened = new PrintAckOutbox(paths, NullLogger<PrintAckOutbox>.Instance);
        var sent = TelemetrySendResult.Retry;
        await reopened.FlushAsync(async batch =>
        {
            sent = await client.SendPrintAcksAsync(batch, E2EConfig.ApiBaseUrl, E2EConfig.ApiKey, deviceId);
            // Drain in the TEST only on a real 2xx — so a 4xx-reject (bad key / contract drift) leaves the
            // ack queued and the drain assertion below fails, instead of a false green. (Production still
            // drops a 4xx to avoid wedging — that's the client's job, not this test's assertion.)
            return sent == TelemetrySendResult.Sent;
        });

        Assert.Equal(TelemetrySendResult.Sent, sent);   // the backend actually ingested it...
        var flushedAgain = 0;
        await reopened.FlushAsync(b => { flushedAgain += b.Count; return Task.FromResult(true); });
        Assert.Equal(0, flushedAgain);                  // ...and the durable queue drained.
    }
}
