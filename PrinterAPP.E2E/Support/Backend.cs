using System.Net;
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

    // Admin JWT resolved once per run: a direct PRINTERAPP_E2E_ADMIN_JWT override wins; otherwise mint a
    // fresh one from PRINTERAPP_E2E_ADMIN_{EMAIL,PASSWORD} via the real login endpoint (no static JWT to
    // expire); null when neither is configured (admin-gated tests then skip). Cached behind a gate so the
    // login round-trip happens at most once even under xUnit's parallel test execution.
    private static readonly SemaphoreSlim AdminGate = new(1, 1);
    private static bool _adminResolved;
    private static string? _adminJwt;

    public static async Task<string?> ResolveAdminJwtAsync()
    {
        if (_adminResolved) return _adminJwt;
        await AdminGate.WaitAsync();
        try
        {
            if (_adminResolved) return _adminJwt;
            _adminJwt = await MintAdminJwtAsync();
            _adminResolved = true;
            return _adminJwt;
        }
        finally { AdminGate.Release(); }
    }

    private static async Task<string?> MintAdminJwtAsync()
    {
        if (!string.IsNullOrWhiteSpace(E2EConfig.AdminJwt))
            return E2EConfig.AdminJwt; // direct override (e.g. a JWT pasted for a one-off run)

        if (string.IsNullOrWhiteSpace(E2EConfig.AdminEmail) || string.IsNullOrWhiteSpace(E2EConfig.AdminPassword))
            return null; // no admin auth configured → admin-gated tests skip

        var body = JsonSerializer.Serialize(new { email = E2EConfig.AdminEmail, password = E2EConfig.AdminPassword });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/api/auth/login")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null; // bad creds / not an admin → skip rather than red-fail the whole suite

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("data", out var data) &&
               data.TryGetProperty("accessToken", out var token)
            ? token.GetString()
            : null;
    }

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
        var jwt = await ResolveAdminJwtAsync();
        if (string.IsNullOrWhiteSpace(jwt))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Root}/api/devices");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// True if the printer-feed rejects an unauthenticated request (401) — i.e. the backend enforces the
    /// X-Api-Key. Lets the positive feed test SKIP (not red-fail) when no key is configured against an
    /// enforcing backend, while a configured-but-wrong key still fails loudly. Any non-401 (incl. an open
    /// dev backend's 200, or an unreachable host) returns false → the test proceeds.
    /// </summary>
    public static async Task<bool> FeedEnforcesKeyAsync()
    {
        try
        {
            using var response = await Http.GetAsync($"{Root}/api/orders/printer-feed");
            return response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
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

    /// <summary>An order created for a test: its Guid id (for print-ack correlation) + its order number
    /// (for feed correlation).</summary>
    public sealed record CreatedOrder(Guid Id, string Number);

    /// <summary>
    /// Creates a DineIn order (auto-confirms → lands in the printer-feed) via POST /api/orders and returns
    /// it, or null with a reason when it can't (no admin JWT to satisfy [Authorize], no products, or a
    /// validation reject). The app never creates orders — the customer frontend does — so this is a
    /// legitimate direct API call, not a shortcut around the behaviour under test.
    /// </summary>
    public static async Task<(CreatedOrder? Order, string Reason)> CreateConfirmedDineInOrderOrNullAsync()
    {
        var jwt = await ResolveAdminJwtAsync();
        if (string.IsNullOrWhiteSpace(jwt))
            return (null, "no admin auth (set PRINTERAPP_E2E_ADMIN_{EMAIL,PASSWORD} or _JWT) — POST /api/orders is [Authorize]");

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
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);

        using var response = await Http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return (null, $"POST /api/orders → {(int)response.StatusCode}: {Truncate(payload)}");

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("orderNumber", out var num) && num.GetString() is { Length: > 0 } orderNumber &&
            data.TryGetProperty("id", out var idEl) && idEl.TryGetGuid(out var id))
        {
            return (new CreatedOrder(id, orderNumber), "created");
        }

        return (null, $"create succeeded but response lacked id/orderNumber: {Truncate(payload)}");
    }

    /// <summary>
    /// Order NUMBERS currently flagged as missed (Confirmed, past grace, no Printed receipt) by
    /// GET /api/devices/missed-orders, or null when no admin JWT is configured. Admin-only read — the app
    /// never displays it. Assert membership by number, never list size (other orders may be present).
    /// </summary>
    public static async Task<IReadOnlyList<string>?> MissedOrderNumbersOrNullAsync(int graceMinutes, int lookbackHours)
    {
        var jwt = await ResolveAdminJwtAsync();
        if (string.IsNullOrWhiteSpace(jwt))
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{Root}/api/devices/missed-orders?graceMinutes={graceMinutes}&lookbackHours={lookbackHours}");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var numbers = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("orderNumber", out var n) && n.GetString() is { Length: > 0 } s)
                    numbers.Add(s);
        }
        return numbers;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
