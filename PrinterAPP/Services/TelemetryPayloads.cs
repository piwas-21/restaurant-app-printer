using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Pure builders that map local state into telemetry request bodies. MAUI-free and side-effect-free
/// so the "only non-PII, never the API key" contract is unit-testable. See the fleet-observability plan.
/// </summary>
public static partial class TelemetryPayloads
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
            TargetCapabilities = PrinterTargetCapabilityBuilder.Build(config),
            KitchenRoutingMode = config.KitchenRoutingMode,
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
        PrinterConfiguration config, DateTime receivedAt, string? deviceId = null)
    {
        if (!Guid.TryParse(order.Id, out var orderId))
            return new List<PrintAck>();

        if (!string.IsNullOrWhiteSpace(deviceId) && order.RoutingStates is { Count: > 0 })
        {
            return RoutedPrintAcks(order, orderId,
                cashier ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                frontKitchen ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                backKitchen ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                KitchenPrintOutcome.Sent, config, receivedAt, deviceId);
        }

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

    /// <summary>
    /// Adds the typed General/Default acknowledgement to the legacy order acknowledgements. This
    /// overload is opt-in so old callers retain their exact three-ack wire shape.
    /// </summary>
    public static List<PrintAck> PrintAcks(
        Order order,
        bool cashier,
        bool frontKitchen,
        bool backKitchen,
        KitchenPrintOutcome generalDefault,
        PrinterConfiguration config,
        DateTime receivedAt,
        string? deviceId = null)
    {
        if (Guid.TryParse(order.Id, out var orderId)
            && !string.IsNullOrWhiteSpace(deviceId)
            && order.RoutingStates is { Count: > 0 })
        {
            return RoutedPrintAcks(order, orderId,
                cashier ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                frontKitchen ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                backKitchen ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                generalDefault, config, receivedAt, deviceId);
        }

        var acks = PrintAcks(order, cashier, frontKitchen, backKitchen, config, receivedAt);
        if (!Guid.TryParse(order.Id, out var legacyOrderId))
            return acks;

        var target = config.KitchenRoutingMode == KitchenRoutingMode.SingleKitchen
            ? DevicePrintTarget.General
            : DevicePrintTarget.Default;
        acks.Add(BuildOutcomeAck(legacyOrderId, target, generalDefault, receivedAt));
        return acks;
    }

    /// <summary>Typed variant used by the routed pipeline so no-work is never reported as Printed.</summary>
    public static List<PrintAck> PrintAcks(
        Order order,
        bool cashier,
        KitchenPrintOutcome frontKitchen,
        KitchenPrintOutcome backKitchen,
        KitchenPrintOutcome generalDefault,
        PrinterConfiguration config,
        DateTime receivedAt,
        string? deviceId = null)
    {
        if (Guid.TryParse(order.Id, out var orderId)
            && !string.IsNullOrWhiteSpace(deviceId)
            && order.RoutingStates is { Count: > 0 })
        {
            return RoutedPrintAcks(order, orderId,
                cashier ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed,
                frontKitchen, backKitchen, generalDefault, config, receivedAt, deviceId);
        }

        var acks = PrintAcks(order, cashier, frontKitchen.IsSuccess, backKitchen.IsSuccess,
            config, receivedAt);
        if (Guid.TryParse(order.Id, out var legacyOrderId))
        {
            var target = config.KitchenRoutingMode == KitchenRoutingMode.SingleKitchen
                ? DevicePrintTarget.General
                : DevicePrintTarget.Default;
            acks.Add(BuildOutcomeAck(legacyOrderId, target, generalDefault, receivedAt));
        }
        return acks;
    }

    /// <summary>
    /// Maps a typed additive UPDATE outcome into a job-aware acknowledgement. Only <see
    /// cref="KitchenPrintStatus.Sent"/> becomes <see cref="DevicePrintStatus.Sent"/>; failures and
    /// unknown/unconfigured routes retain their explicit status and are never upgraded to success.
    /// </summary>
    public static PrintAck UpdateAck(
        PrinterFeedUpdate update,
        KitchenPrintOutcome outcome,
        DateTime receivedAt)
    {
        ArgumentNullException.ThrowIfNull(update);
        var status = outcome.Status switch
        {
            KitchenPrintStatus.Sent => DevicePrintStatus.Sent,
            KitchenPrintStatus.Skipped => DevicePrintStatus.Skipped,
            KitchenPrintStatus.Failed => DevicePrintStatus.Failed,
            KitchenPrintStatus.NotConfigured => DevicePrintStatus.NotConfigured,
            KitchenPrintStatus.Unknown => DevicePrintStatus.Unknown,
            _ => DevicePrintStatus.Unknown,
        };

        return new PrintAck
        {
            OrderId = update.OrderId,
            Target = update.Target,
            Status = status,
            ReceivedAt = receivedAt,
            PrintedAt = status == DevicePrintStatus.Sent ? DateTime.UtcNow : null,
            FailureReason = status is DevicePrintStatus.Failed
                or DevicePrintStatus.NotConfigured
                or DevicePrintStatus.Unknown
                ? status.ToString()
                : null,
            Copies = status == DevicePrintStatus.Sent ? 1 : 0,
            JobId = update.JobId,
            Revision = update.Revision,
            JobType = update.JobType,
        };
    }

    /// <summary>Queues a durable lifecycle acknowledgement for an update job.</summary>
    public static PrintAck UpdateQueuedAck(PrinterFeedUpdate update, DateTime receivedAt) =>
        new()
        {
            OrderId = update.OrderId,
            Target = update.Target,
            Status = DevicePrintStatus.Queued,
            ReceivedAt = receivedAt,
            Copies = 0,
            JobId = update.JobId,
            Revision = update.Revision,
            JobType = update.JobType,
        };

    private static PrintAck BuildOutcomeAck(
        Guid orderId,
        DevicePrintTarget target,
        KitchenPrintOutcome outcome,
        DateTime receivedAt,
        OrderRoutingState? route = null)
    {
        var status = outcome.Status switch
        {
            KitchenPrintStatus.Sent => DevicePrintStatus.Printed,
            KitchenPrintStatus.Skipped => DevicePrintStatus.Skipped,
            KitchenPrintStatus.Failed => DevicePrintStatus.Failed,
            KitchenPrintStatus.NotConfigured => DevicePrintStatus.NotConfigured,
            KitchenPrintStatus.Unknown => DevicePrintStatus.Unknown,
            _ => DevicePrintStatus.Unknown,
        };
        return new PrintAck
        {
            OrderId = orderId,
            Target = target,
            Status = status,
            ReceivedAt = receivedAt,
            PrintedAt = status == DevicePrintStatus.Printed ? DateTime.UtcNow : null,
            FailureReason = status is DevicePrintStatus.Failed
                or DevicePrintStatus.NotConfigured
                or DevicePrintStatus.Unknown
                ? status.ToString()
                : null,
            Copies = status == DevicePrintStatus.Printed ? 1 : 0,
            JobId = route?.JobId,
            Revision = route?.Revision,
            JobType = route is null ? null : DevicePrintJobType.Order,
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
