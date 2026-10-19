using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Consumes each server claim once. Neither a crash nor a lost acknowledgement replays output.</summary>
public sealed class CashierPrintJobClient(HttpClient http, IPrinterService printer,
    IOrderPrintService printing, IDeviceIdentityService identity,
    ILogger<CashierPrintJobClient> logger) : ICashierPrintJobClient
{
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await printer.LoadConfigurationAsync();
            if (string.IsNullOrWhiteSpace(config.CashierPrinterName)
                || string.IsNullOrWhiteSpace(config.ApiBaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey)) return;
            var allowAutomatic = AllowsAutomatic(config, DateTime.Now.TimeOfDay);
            using var request = Request(config, $"claim?allowAutomatic={allowAutomatic.ToString().ToLowerInvariant()}");
            using var response = await SendAsync(request, config, cancellationToken);
            if (!response.IsSuccessStatusCode) return; // Compatible with servers before this protocol.
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!body.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return;
            var job = data.Deserialize<CashierPrintJob>(PrinterJsonSerialization.Options);
            if (job is not { JobId: var id, Status: "Claimed", Receipt: { } receipt } || id == Guid.Empty) return;
            // Manual print sends to CASHIER ONLY and bypasses automatic-print restrictions. A close
            // job was already checked against this device's auto-print setting by the server.
            var status = "Unknown";
            try
            {
                status = await printing.PrintOrderAsync(receipt, PrinterType.Cashier,
                    isManualPrint: true, cancellationToken) ? "Sent" : "Unknown";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cashier bill {JobId} has an unknown print outcome", id);
            }
            using var acknowledgement = Request(config, $"{id:D}/complete");
            acknowledgement.Content = JsonContent.Create(new { status });
            using var acknowledged = await SendAsync(acknowledgement, config, cancellationToken);
            if (!acknowledged.IsSuccessStatusCode)
                logger.LogWarning("Cashier bill {JobId} outcome acknowledgement failed; held against duplicate output", id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Cashier bill polling stopped; claimed output remains held against duplicates");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cashier bill queue unavailable; any claimed output remains held against duplicates");
        }
    }

    internal static bool AllowsAutomatic(PrinterConfiguration config, TimeSpan now)
    {
        if (!config.CashierAutoPrint) return false;
        if (!config.EnableTimeRestriction) return true;
        return config.RestrictStartTime < config.RestrictEndTime
            ? now < config.RestrictStartTime || now >= config.RestrictEndTime
            : now < config.RestrictStartTime && now >= config.RestrictEndTime;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        PrinterConfiguration config, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(
            PrinterConfiguration.MinimumCashierPrintJobTimeoutSeconds, config.CashierPrintJobTimeoutSeconds)));
        return await http.SendAsync(request, timeout.Token);
    }

    private HttpRequestMessage Request(PrinterConfiguration config, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{config.ApiBaseUrl.TrimEnd('/')}/api/cashier-print-jobs/{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", config.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Device-Id", identity.DeviceId);
        return request;
    }
}
