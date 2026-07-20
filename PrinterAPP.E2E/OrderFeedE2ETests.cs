using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.E2E.Support;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.E2E;

/// <summary>
/// Order-feed E2E (A1): the REAL <see cref="EventStreamingService"/> polling loop against a REAL backend
/// (no mocks of our code — only the config-source + log-sink edges are doubled). Proves the wiring the
/// telemetry tests don't: printer-feed URL composition, X-Api-Key auth, per-order deserialisation, the
/// no-wedge poll cycle, and — when a seeded backend + admin JWT are available — that an order placed
/// through the backend actually reaches the printer path. See docs/E2E-STRATEGY.md.
/// </summary>
public class OrderFeedE2ETests
{
    private static PrinterConfiguration Config(string? apiKeyOverride = null) => new()
    {
        ApiBaseUrl = E2EConfig.ApiBaseUrl,
        ApiKey = apiKeyOverride ?? E2EConfig.ApiKey,
        TenantSlug = E2EConfig.TenantSlug,
        DeviceLabel = "E2E order-feed device",
        CashierPrinterName = "127.0.0.1:9100",
    };

    private static EventStreamingService NewFeed(PrinterConfiguration config, out NoopRequestLogService log)
    {
        log = new NoopRequestLogService();
        return new EventStreamingService(
            new TestPrinterService(config),
            log,
            NullLogger<EventStreamingService>.Instance);
    }

    // Poll the condition until it holds or the timeout elapses (the feed polls every 5s on a background task).
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                return false;
            try { await Task.Delay(500, cts.Token); }
            catch (TaskCanceledException) { return condition(); }
        }
        return true;
    }

    // 🔴 HIGH — the real feed polls the real printer-feed endpoint with the configured key and advances its
    // success timestamp: proves URL + auth + parse + no-wedge end to end (the whole path unit tests can't).
    [SkippableFact]
    public async Task OrderFeed_RealService_PollsRealBackend_AndAdvancesTimestamp()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");
        // Symmetric with the negative test: if no key is configured but the feed enforces one, the
        // positive path can't be exercised — skip rather than red-fail. (A configured-but-wrong key is
        // NOT skipped — it fails loudly below, which is real auth-drift detection.)
        Skip.If(string.IsNullOrWhiteSpace(E2EConfig.ApiKey) && await Backend.FeedEnforcesKeyAsync(),
            "feed enforces X-Api-Key but PRINTERAPP_E2E_API_KEY is unset — nothing to prove");

        var feed = NewFeed(Config(), out _);
        try
        {
            await feed.StartListeningAsync();

            // First poll fires after a 5s delay; allow a few cycles for a slow/cold backend.
            var polled = await WaitUntilAsync(() => feed.LastSuccessfulPollAt is not null, TimeSpan.FromSeconds(25));

            Assert.True(polled,
                "the real feed never completed a successful poll (LastSuccessfulPollAt stayed null) — " +
                "check the backend is reachable and PRINTERAPP_E2E_API_KEY is accepted");
            Assert.True(feed.IsListening, "the feed stopped listening (wedged) after starting");
        }
        finally
        {
            await feed.StopListeningAsync();
        }
    }

    // 🔴 HIGH — a wrong API key must NOT poll successfully, and the failure must surface on the log/errors
    // surface (the field's only diagnostic trail). Only meaningful when the backend enforces the key, so it
    // skips against an open (keyless) local backend.
    [SkippableFact]
    public async Task OrderFeed_RealService_WithBadApiKey_DoesNotAdvance_AndSurfacesAuthError()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");
        Skip.If(string.IsNullOrWhiteSpace(E2EConfig.ApiKey),
            "backend isn't key-enforcing here (no PRINTERAPP_E2E_API_KEY) — the bad-key path is a no-op");

        var feed = NewFeed(Config(apiKeyOverride: "e2e-bad-key-" + Guid.NewGuid().ToString("N")), out var log);
        try
        {
            await feed.StartListeningAsync();

            // Give it more than two poll cycles to attempt (and fail) the feed.
            await Task.Delay(TimeSpan.FromSeconds(13));

            Assert.Null(feed.LastSuccessfulPollAt); // never a 2xx with a bad key
            Assert.Contains(log.Errors, e => e.Operation == "Order Polling");
        }
        finally
        {
            await feed.StopListeningAsync();
        }
    }

    // 🔴 HIGH — the true loop: an order placed through the backend reaches the printer path. Requires a
    // seeded backend + an admin JWT to create the order; skips (never red-fails) when either is missing.
    [SkippableFact]
    public async Task OrderFeed_DeliversCreatedOrder_ToPrinter()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");

        var (orderNumber, reason) = await Backend.CreateConfirmedDineInOrderOrNullAsync();
        Skip.If(orderNumber is null, $"couldn't create an order to deliver: {reason}");

        var received = new ConcurrentBag<Order>();
        var feed = NewFeed(Config(), out _);
        feed.OrderReceived += (_, e) => { if (e.Order is not null) received.Add(e.Order); };
        try
        {
            await feed.StartListeningAsync();

            var delivered = await WaitUntilAsync(
                () => received.Any(o => o.OrderNumber == orderNumber || o.OrderNumber.EndsWith(orderNumber!)),
                TimeSpan.FromSeconds(40));

            Assert.True(delivered,
                $"order {orderNumber} was created (Confirmed DineIn) but never reached the feed within 40s; " +
                $"feed saw [{string.Join(", ", received.Select(o => o.OrderNumber))}]");

            var match = received.First(o => o.OrderNumber == orderNumber || o.OrderNumber.EndsWith(orderNumber!));
            Assert.Equal("Confirmed", match.Status, ignoreCase: true);
        }
        finally
        {
            await feed.StopListeningAsync();
        }
    }
}
