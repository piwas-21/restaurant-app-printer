namespace PrinterAPP.Services;

/// <summary>
/// Stable identity of this printer-app installation, for fleet telemetry + Sentry tagging.
/// <see cref="DeviceId"/> distinguishes multiple devices at one site (e.g. kitchen vs cashier
/// tablet) that share the tenant's single <c>X-Api-Key</c> — the key identifies the tenant, not
/// the device. See docs/plans/PRINTER-APP-FLEET-OBSERVABILITY-PLAN.md.
/// </summary>
public interface IDeviceIdentityService
{
    /// <summary>Stable per-install GUID, generated once and persisted. Survives app updates; reset only on uninstall / clear-data.</summary>
    string DeviceId { get; }

    /// <summary>Runtime platform label, e.g. "Android" or "WinUI".</summary>
    string Platform { get; }

    /// <summary>App display version, e.g. "1.0.18".</summary>
    string AppVersion { get; }

    /// <summary>
    /// Applies this device's identity — plus the given tenant slug — as Sentry scope tags so fleet
    /// errors are attributable to a specific installation. No-op when Sentry is inert (no DSN). Call
    /// after startup (e.g. once config has loaded), so the lazily-resolved <see cref="DeviceId"/>
    /// persists correctly.
    /// </summary>
    void ApplySentryTags(string tenantSlug);
}
