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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
