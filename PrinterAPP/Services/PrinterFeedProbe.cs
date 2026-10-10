using System.Net;

namespace PrinterAPP.Services;

/// <summary>The shared connection test uses the same feed URL and API-key header as order polling.</summary>
public static class PrinterFeedProbe
{
    private const int DefaultRequestTimeoutSeconds = 10;

    public static async Task<HttpStatusCode?> TestAsync(
        string apiUrl, string? apiKey, TimeSpan? requestTimeout = null)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = requestTimeout ?? TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds)
            };
            var url = $"{apiUrl.TrimEnd('/')}/api/orders/printer-feed?modifiedSince={DateTime.UtcNow:o}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Add("X-Api-Key", apiKey);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
