using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Pure builders that map local state into telemetry request bodies. MAUI-free and side-effect-free
/// so the "only non-PII, never the API key" contract is unit-testable. See the fleet-observability plan.
/// </summary>
public static class TelemetryPayloads
{
    public static HeartbeatRequest Heartbeat(
        PrinterConfiguration config, string platform, string appVersion,
        bool feedRunning, DateTime? lastSuccessfulPollAt)
    {
        return new HeartbeatRequest
        {
            Label = NullIfBlank(config.DeviceLabel),
            TenantSlug = NullIfBlank(config.TenantSlug),
            Platform = NullIfBlank(platform),
            AppVersion = NullIfBlank(appVersion),
            FeedRunning = feedRunning,
            LastSuccessfulPollAt = lastSuccessfulPollAt,
            ApiBaseUrl = NullIfBlank(config.ApiBaseUrl),
            // config.ApiKey is DELIBERATELY not read here — the heartbeat body must carry no secret.
            KitchenPrinter = ComposeKitchenPrinter(config),
            CashierPrinter = NullIfBlank(config.CashierPrinterName),
        };
    }

    // The device has up to three kitchen targets (front/back/legacy); summarise the configured ones
    // as a single non-secret descriptor for the admin config view.
    private static string? ComposeKitchenPrinter(PrinterConfiguration config)
    {
        var parts = new[]
        {
            config.FrontKitchenPrinterName,
            config.BackKitchenPrinterName,
            config.KitchenPrinterName,
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct();

        return NullIfBlank(string.Join(", ", parts));
    }

    /// <summary>
    /// Maps one order's per-target print outcomes into acks. <c>Skipped</c> when the target has no
    /// printer configured (mirroring OrderPrintService's fallbacks: front→cashier, back→legacy kitchen)
    /// OR auto-print is disabled for it — the device deliberately didn't print. Otherwise
    /// <c>Printed</c>/<c>Failed</c> from the success bool. Returns empty if the order id isn't a GUID.
    /// <para>Known limitation: OrderPrintService returns a bare success bool that also reads <c>true</c>
    /// for "no items routed to this kitchen" and (with time restrictions) "outside the print window",
    /// which this can't distinguish from a real print — those still surface as <c>Printed</c>. Full
    /// fidelity needs OrderPrintService to return a per-target status; tracked as a follow-up.</para>
    /// </summary>
    public static List<PrintAck> PrintAcks(
        Order order, bool cashier, bool frontKitchen, bool backKitchen,
        PrinterConfiguration config, DateTime receivedAt)
    {
        if (!Guid.TryParse(order.Id, out var orderId))
            return new List<PrintAck>();

        return new List<PrintAck>
        {
            BuildAck(orderId, DevicePrintTarget.Cashier, cashier,
                config.CashierPrinterName, config.CashierAutoPrint, config.CashierPrintCopies, receivedAt),
            BuildAck(orderId, DevicePrintTarget.FrontKitchen, frontKitchen,
                FirstNonBlank(config.FrontKitchenPrinterName, config.CashierPrinterName),
                config.FrontKitchenAutoPrint, config.KitchenPrintCopies, receivedAt),
            BuildAck(orderId, DevicePrintTarget.BackKitchen, backKitchen,
                FirstNonBlank(config.BackKitchenPrinterName, config.KitchenPrinterName),
                config.BackKitchenAutoPrint, config.KitchenPrintCopies, receivedAt),
        };
    }

    private static PrintAck BuildAck(
        Guid orderId, DevicePrintTarget target, bool success,
        string? printerName, bool autoPrint, int copies, DateTime receivedAt)
    {
        // "Would print" = a printer is configured AND auto-print is on; otherwise the device didn't
        // (and wasn't going to) print → Skipped, not a fabricated Printed.
        var willPrint = !string.IsNullOrWhiteSpace(printerName) && autoPrint;
        var status = (willPrint, success) switch
        {
            (false, _) => DevicePrintStatus.Skipped,   // no printer or auto-print off → didn't print
            (true, true) => DevicePrintStatus.Printed,
            (true, false) => DevicePrintStatus.Failed,
        };

        return new PrintAck
        {
            OrderId = orderId,
            Target = target,
            Status = status,
            ReceivedAt = receivedAt,
            PrintedAt = status == DevicePrintStatus.Printed ? DateTime.UtcNow : null,
            FailureReason = status == DevicePrintStatus.Failed ? "Print failed" : null,
            Copies = status == DevicePrintStatus.Printed ? Math.Max(1, copies) : 0,
        };
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
