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

    /// <summary>True if the backend answers /api/health — used to skip the suite when none is up.</summary>
    public static async Task<bool> IsReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{Root}/api/health");
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

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EConfig.AdminJwt);
        return await client.GetStringAsync($"{Root}/api/devices");
    }
}
