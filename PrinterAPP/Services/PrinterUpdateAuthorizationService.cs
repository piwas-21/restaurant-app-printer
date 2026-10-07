using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public sealed class PrinterUpdateAuthorizationService : IPrinterUpdateAuthorizationService
{
    private readonly HttpClient _httpClient;
    private readonly IPrinterService _printerService;
    private readonly ILogger<PrinterUpdateAuthorizationService> _logger;

    public PrinterUpdateAuthorizationService(
        HttpClient httpClient,
        IPrinterService printerService,
        ILogger<PrinterUpdateAuthorizationService> logger)
    {
        _httpClient = httpClient;
        _printerService = printerService;
        _logger = logger;
    }

    public async Task<PrinterUpdateAuthorizationResult> CheckAsync(
        PrinterFeedUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (update.IsWithdrawn || update.JobId == Guid.Empty || update.Revision != 1
            || !UpdateJobRouting.IsUpdateTarget(update.Target))
            return PrinterUpdateAuthorizationResult.Unavailable;

        try
        {
            var config = await _printerService.LoadConfigurationAsync();
            if (string.IsNullOrWhiteSpace(config.ApiBaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
                return PrinterUpdateAuthorizationResult.Unavailable;

            var target = Uri.EscapeDataString(update.Target.ToString());
            var url = $"{config.ApiBaseUrl.TrimEnd('/')}/api/printer-feed/updates/{update.JobId:D}/authorization"
                + $"?revision={update.Revision}&target={target}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-Api-Key", config.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Print authorization preflight for job {JobId} returned {StatusCode}; no output will be sent",
                    update.JobId, response.StatusCode);
                return PrinterUpdateAuthorizationResult.Unavailable;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = ParseResponse(body, update);
            if (result.Status == PrinterUpdateAuthorizationStatus.Unavailable)
                _logger.LogWarning(
                    "Print authorization preflight for job {JobId} did not match the requested identity; no output will be sent",
                    update.JobId);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Print authorization preflight failed for job {JobId}; no output will be sent", update.JobId);
            return PrinterUpdateAuthorizationResult.Unavailable;
        }
    }

    internal static PrinterUpdateAuthorizationResult ParseResponse(
        string body,
        PrinterFeedUpdate expected)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryProperty(root, "success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !TryProperty(root, "data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !TryProperty(data, "jobId", out var jobId)
                || jobId.ValueKind != JsonValueKind.String
                || !Guid.TryParse(jobId.GetString(), out var parsedJobId)
                || parsedJobId != expected.JobId
                || !TryProperty(data, "revision", out var revision)
                || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt32(out var parsedRevision)
                || parsedRevision != expected.Revision
                || !TryProperty(data, "target", out var target)
                || target.ValueKind != JsonValueKind.String
                || !string.Equals(target.GetString(), expected.Target.ToString(), StringComparison.Ordinal)
                || !TryProperty(data, "status", out var status)
                || status.ValueKind != JsonValueKind.String)
                return PrinterUpdateAuthorizationResult.Unavailable;

            return status.GetString() switch
            {
                "Authorized" => PrinterUpdateAuthorizationResult.Authorized,
                "Withdrawn" => PrinterUpdateAuthorizationResult.Withdrawn,
                _ => PrinterUpdateAuthorizationResult.Unavailable,
            };
        }
        catch (JsonException)
        {
            return PrinterUpdateAuthorizationResult.Unavailable;
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
