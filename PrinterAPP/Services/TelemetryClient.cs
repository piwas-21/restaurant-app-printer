using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// HTTP <see cref="ITelemetryClient"/>. Pure and MAUI-free (source-linked into the test project):
/// composes the request, sets the auth + device headers, POSTs, and maps the outcome to a bool.
/// Never throws — a telemetry failure must not degrade printing.
/// </summary>
public class TelemetryClient : ITelemetryClient
{
    // camelCase + omit nulls + enums as their names ("FrontKitchen"/"Printed"), matching the backend.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<TelemetryClient> _logger;

    public TelemetryClient(HttpClient httpClient, ILogger<TelemetryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> SendHeartbeatAsync(
        HeartbeatRequest request, string apiBaseUrl, string apiKey, string deviceId,
        CancellationToken cancellationToken = default)
    {
        // Nothing to send to, or no identity to send as — skip silently (not an error).
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(deviceId))
            return false;

        try
        {
            var url = $"{apiBaseUrl.TrimEnd('/')}/api/devices/heartbeat";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            // Tenant auth (same key as the printer-feed) + per-install device id. The key is a header,
            // never part of the JSON body — the body carries no secret.
            if (!string.IsNullOrWhiteSpace(apiKey))
                httpRequest.Headers.Add("X-Api-Key", apiKey);
            httpRequest.Headers.Add("X-Device-Id", deviceId);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Heartbeat POST returned {StatusCode}", response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;   // app shutting down / timeout — nothing to log.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat POST failed");
            return false;
        }
    }

    public async Task<TelemetrySendResult> SendPrintAcksAsync(
        IReadOnlyList<PrintAck> acks, string apiBaseUrl, string apiKey, string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (acks.Count == 0)
            return TelemetrySendResult.Sent;   // nothing pending → treat as done, drop from outbox.

        // Can't send yet (unconfigured) — keep the batch for a later cycle rather than dropping it.
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(deviceId))
            return TelemetrySendResult.Retry;

        try
        {
            var url = $"{apiBaseUrl.TrimEnd('/')}/api/devices/print-acks";
            // Shape matches RecordPrintAcksCommand { Acks }.
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { acks }, options: JsonOptions),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                httpRequest.Headers.Add("X-Api-Key", apiKey);
            httpRequest.Headers.Add("X-Device-Id", deviceId);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
                return TelemetrySendResult.Sent;

            // 4xx = permanent (bad batch / auth): drop it so one bad batch can't wedge the outbox.
            // EXCEPT 408 (timeout) + 429 (rate-limited) — those are transient 4xx, and the backend
            // runs a rate limiter, so a busy fleet must keep and retry rather than lose acks.
            if ((int)response.StatusCode is >= 400 and < 500
                && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
            {
                _logger.LogWarning("Print-acks rejected {StatusCode} — dropping batch.", response.StatusCode);
                return TelemetrySendResult.Rejected;
            }

            return TelemetrySendResult.Retry;   // 5xx / 408 / 429 — transient.
        }
        catch (OperationCanceledException)
        {
            return TelemetrySendResult.Retry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Print-acks POST failed");
            return TelemetrySendResult.Retry;
        }
    }
}
