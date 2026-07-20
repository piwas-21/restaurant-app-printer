using System.Text;
using System.Text.Json;

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

    /// <summary>
    /// The first seeded product (id + base price) from the anonymous catalogue, or null when the backend
    /// has none seeded (staging ships 0 products). The order-delivery test needs a real product to build a
    /// valid order — the customer frontend's job, which the printer-app has no path for.
    /// </summary>
    public static async Task<(string Id, decimal Price)?> FirstProductOrNullAsync()
    {
        using var response = await Http.GetAsync($"{Root}/api/products?page=1&pageSize=1");
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
        {
            return null;
        }

        var first = items[0];
        var id = first.GetProperty("id").GetString();
        var price = first.TryGetProperty("basePrice", out var p) ? p.GetDecimal() : 0m;
        return string.IsNullOrEmpty(id) ? null : (id, price);
    }

    /// <summary>
    /// Creates a DineIn order (auto-confirms → lands in the printer-feed) via POST /api/orders and returns
    /// its order number, or null with a reason when it can't (no admin JWT to satisfy [Authorize], no
    /// products, or a validation reject). The app never creates orders — the customer frontend does — so
    /// this is a legitimate direct API call, not a shortcut around the behaviour under test.
    /// </summary>
    public static async Task<(string? OrderNumber, string Reason)> CreateConfirmedDineInOrderOrNullAsync()
    {
        if (string.IsNullOrWhiteSpace(E2EConfig.AdminJwt))
            return (null, "no PRINTERAPP_E2E_ADMIN_JWT (POST /api/orders is [Authorize])");

        var product = await FirstProductOrNullAsync();
        if (product is null)
            return (null, "backend has no seeded products to build an order from");

        var body = JsonSerializer.Serialize(new
        {
            type = "DineIn",
            tableNumber = 1,
            items = new[]
            {
                new { productId = product.Value.Id, quantity = 1, unitPrice = product.Value.Price },
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/api/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EConfig.AdminJwt);

        using var response = await Http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return (null, $"POST /api/orders → {(int)response.StatusCode}: {Truncate(payload)}");

        using var doc = JsonDocument.Parse(payload);
        var orderNumber = doc.RootElement.TryGetProperty("data", out var data) &&
                          data.TryGetProperty("orderNumber", out var num)
            ? num.GetString()
            : null;

        return string.IsNullOrEmpty(orderNumber)
            ? (null, $"create succeeded but no orderNumber in response: {Truncate(payload)}")
            : (orderNumber, "created");
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
