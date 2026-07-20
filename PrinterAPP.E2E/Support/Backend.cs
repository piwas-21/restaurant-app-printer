namespace PrinterAPP.E2E.Support;

/// <summary>
/// Thin backend access for the two things the printer-app has NO path for (docs/E2E-STRATEGY.md
/// §Direct API calls): a reachability probe (to skip gracefully when no backend is up) and the
/// admin fleet read used to *assert* (the app never displays it). Everything else in the suite
/// drives the real printer-app services.
/// </summary>
public static class Backend
{
    private static string Root => E2EConfig.ApiBaseUrl.TrimEnd('/');

    // One shared client; auth is set per-request (never on DefaultRequestHeaders) so it stays
    // thread-safe and doesn't churn sockets.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>True if the backend answers /api/health — used to skip the suite when none is up.</summary>
    public static async Task<bool> IsReachableAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await Http.GetAsync($"{Root}/api/health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The admin device roster (GET /api/devices), or null when no admin JWT is configured (the
    /// caller then relies on the client-side 2xx assertion alone). Requires PRINTERAPP_E2E_ADMIN_JWT.
    /// </summary>
    public static async Task<string?> DevicesJsonOrNullAsync()
    {
        if (string.IsNullOrWhiteSpace(E2EConfig.AdminJwt))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Root}/api/devices");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EConfig.AdminJwt);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
