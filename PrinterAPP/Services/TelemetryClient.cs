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
    // camelCase + omit nulls, matching the backend's System.Text.Json defaults.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
}
