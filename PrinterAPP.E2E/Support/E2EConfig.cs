namespace PrinterAPP.E2E.Support;

/// <summary>
/// Environment-parameterized E2E config (docs/E2E-STRATEGY.md §Environment parameterization). Every
/// base URL + credential comes from a PRINTERAPP_E2E_* env var with a safe local default, so the same
/// suite runs against a CI-spun-up backend, staging, or demo by changing env vars only.
/// </summary>
public static class E2EConfig
{
    public static string ApiBaseUrl => Env("PRINTERAPP_E2E_API_BASE_URL", "http://localhost:5221");
    public static string ApiKey => Env("PRINTERAPP_E2E_API_KEY", "");
    public static string AdminJwt => Env("PRINTERAPP_E2E_ADMIN_JWT", "");
    public static string TenantSlug => Env("PRINTERAPP_E2E_TENANT_SLUG", "rumi");

    /// <summary>A fresh per-test device id so parallel runs + reruns never collide.</summary>
    public static string NewDeviceId() => "e2e-" + Guid.NewGuid().ToString("N");

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
