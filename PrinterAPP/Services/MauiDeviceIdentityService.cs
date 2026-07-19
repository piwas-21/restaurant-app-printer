using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Sentry;

namespace PrinterAPP.Services;

/// <summary>
/// MAUI-backed <see cref="IDeviceIdentityService"/>. <see cref="DeviceId"/> is a GUID stored in a
/// plain <c>device-id</c> file under the app-data root (per-install, cleared only on uninstall /
/// clear-data). A file — not <c>Preferences</c> — because MAUI's Preferences write is async and was
/// not persisting reliably here; <see cref="File.WriteAllText(string,string)"/> flushes synchronously.
/// MAUI-coupled via AppInfo/DeviceInfo, so — like <see cref="MauiAppDataPathProvider"/> — it lives
/// behind the interface and is never linked into the plain-net10 test project.
/// </summary>
public class MauiDeviceIdentityService : IDeviceIdentityService
{
    private const string DeviceIdFileName = "device-id";
    private readonly IAppDataPathProvider _paths;
    private string? _deviceId;

    public MauiDeviceIdentityService(IAppDataPathProvider paths) => _paths = paths;

    // Resolved lazily on first access (after startup), so file I/O runs when the app-data root is
    // ready rather than during MauiProgram construction.
    public string DeviceId => _deviceId ??= ResolveDeviceId();

    public string Platform => DeviceInfo.Current.Platform.ToString();

    public string AppVersion => AppInfo.Current.VersionString;

    public void ApplySentryTags(string tenantSlug)
    {
        // Observability must never break the app: swallow any failure resolving identity or tagging.
        try
        {
            // Resolve eagerly (this also creates+persists the device-id file) rather than inside the
            // ConfigureScope callback, which Sentry may defer/skip — so the id exists every launch,
            // even when Sentry is inert.
            var deviceId = DeviceId;
            var platform = Platform;
            var appVersion = AppVersion;
            var slug = string.IsNullOrWhiteSpace(tenantSlug) ? "unset" : tenantSlug;
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag("device.id", deviceId);
                scope.SetTag("app.platform", platform);
                scope.SetTag("app.version", appVersion);
                scope.SetTag("tenant.slug", slug);
            });
        }
        catch
        {
            // ignored — a telemetry-tagging failure is never worth degrading startup.
        }
    }

    private string ResolveDeviceId()
    {
        var path = Path.Combine(_paths.AppDataDirectory, DeviceIdFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                    return existing;
            }
        }
        catch
        {
            // Unreadable → regenerate below. Never throw from identity resolution.
        }

        var generated = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(path, generated);
        }
        catch
        {
            // Best-effort: an unwritable store just means a fresh id next launch, never a crash.
        }
        return generated;
    }
}
