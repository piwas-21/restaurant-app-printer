using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Sends fleet telemetry to this device's backend (the same host + <c>X-Api-Key</c> the printer-feed
/// uses, plus the per-install <c>X-Device-Id</c> header). Observability must never break the app, so
/// every send is best-effort: failures are logged and reported as <c>false</c>, never thrown.
/// </summary>
public interface ITelemetryClient
{
    /// <summary>POSTs a heartbeat. Returns <c>true</c> on a 2xx, <c>false</c> on any error/non-2xx.</summary>
    Task<bool> SendHeartbeatAsync(
        HeartbeatRequest request, string apiBaseUrl, string apiKey, string deviceId,
        CancellationToken cancellationToken = default);
}
