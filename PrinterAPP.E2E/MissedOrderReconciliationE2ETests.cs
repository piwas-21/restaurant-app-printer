using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.E2E.Support;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.E2E;

/// <summary>
/// Missed-order reconciliation E2E (A2): the fleet-observability guarantee from the 2026-07-19 incident —
/// a Confirmed order the device printed is NOT flagged missed, while one it never printed IS. Drives the
/// REAL <see cref="TelemetryClient"/> print-ack path (which the backend turns into a Printed
/// DeviceOrderReceipt) and asserts against the admin GET /api/devices/missed-orders read. See
/// docs/E2E-STRATEGY.md. Deterministic — no feed/polling involved: create two, ack one, query.
/// </summary>
public class MissedOrderReconciliationE2ETests
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    // 🔴 HIGH — served-but-unprinted surfaces as missed; a printed one does not.
    [SkippableFact]
    public async Task MissedOrders_UnackedConfirmedOrder_IsFlagged_WhileAckedOrderIsNot()
    {
        Skip.IfNot(await Backend.IsReachableAsync(), $"No backend at {E2EConfig.ApiBaseUrl}");
        // Both order creation and the missed-orders read are admin-only; without a JWT there's nothing to do.
        Skip.If(string.IsNullOrWhiteSpace(E2EConfig.AdminJwt),
            "no PRINTERAPP_E2E_ADMIN_JWT (order creation + missed-orders read are admin-only)");
        // The print-ack ingest is X-Api-Key'd; skip on a key-enforcing backend with no key configured.
        Skip.If(string.IsNullOrWhiteSpace(E2EConfig.ApiKey) && await Backend.FeedEnforcesKeyAsync(),
            "backend enforces X-Api-Key but PRINTERAPP_E2E_API_KEY is unset — can't ingest the print-ack");

        // Two Confirmed DineIn orders: one we'll ack as Printed, one we'll leave unprinted.
        var (printed, printedReason) = await Backend.CreateConfirmedDineInOrderOrNullAsync();
        Skip.If(printed is null, $"couldn't create the 'printed' order: {printedReason}");
        var (unprinted, unprintedReason) = await Backend.CreateConfirmedDineInOrderOrNullAsync();
        Skip.If(unprinted is null, $"couldn't create the 'unprinted' order: {unprintedReason}");

        // Ack ONLY the first order, through the real client (backend records a Printed DeviceOrderReceipt).
        var deviceId = E2EConfig.NewDeviceId();
        var ack = new PrintAck
        {
            OrderId = printed!.Id,
            Target = DevicePrintTarget.Cashier,
            Status = DevicePrintStatus.Printed,
            ReceivedAt = DateTime.UtcNow,
            PrintedAt = DateTime.UtcNow,
            Copies = 1,
        };
        var sent = await new TelemetryClient(SharedHttp, NullLogger<TelemetryClient>.Instance)
            .SendPrintAcksAsync(new[] { ack }, E2EConfig.ApiBaseUrl, E2EConfig.ApiKey, deviceId);
        Assert.Equal(TelemetrySendResult.Sent, sent);

        // grace=0 so the just-created orders are immediately eligible; lookback 24h covers them.
        var missed = await Backend.MissedOrderNumbersOrNullAsync(graceMinutes: 0, lookbackHours: 24);
        Skip.If(missed is null, "no admin JWT for the missed-orders read");

        // Assert by membership, never by list size — staging may carry other unprinted orders.
        Assert.Contains(unprinted!.Number, missed!);        // never printed → missed
        Assert.DoesNotContain(printed.Number, missed!);     // acked Printed → reconciled, not missed
    }
}
