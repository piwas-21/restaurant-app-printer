using PrinterAPP.Services;

namespace PrinterAPP.Models;

/// <summary>
/// Body of <c>POST /api/devices/heartbeat</c>. Mirrors the backend <c>RecordHeartbeatCommand</c>
/// exactly (minus <c>DeviceId</c>, which travels in the <c>X-Device-Id</c> header, not the body).
/// Carries only non-secret fleet status + config — <b>never</b> the <c>X-Api-Key</c> value or any
/// customer PII. See docs/plans/PRINTER-APP-FLEET-OBSERVABILITY-PLAN.md.
/// </summary>
public class HeartbeatRequest
{
    public string? Label { get; set; }
    public string? TenantSlug { get; set; }
    public string? Platform { get; set; }
    public string? AppVersion { get; set; }
    // Nullable to mirror the backend RecordHeartbeatCommand exactly (§5.3); the client always knows
    // the feed state, so it's populated non-null in practice.
    public bool? FeedRunning { get; set; }
    public DateTime? LastSuccessfulPollAt { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? KitchenPrinter { get; set; }
    public string? CashierPrinter { get; set; }
    public List<PrinterTargetCapability>? TargetCapabilities { get; set; }
    public KitchenRoutingMode? KitchenRoutingMode { get; set; }
    /// <summary>True only for clients that enforce server authorization before correction output.</summary>
    public bool SupportsUpdateAuthorization { get; set; }
}
