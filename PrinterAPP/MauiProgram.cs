using System.Reflection;
using Microsoft.Extensions.Logging;
using PrinterAPP.Services;
using Sentry.Maui;

namespace PrinterAPP
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Fleet observability (Phase 0): env-gated Sentry — inert unless a DSN was injected at
            // build time (see PrinterAPP.csproj SentryDsn). Mirrors the backend/frontend/sofra
            // SENTRY_DSN gating. Sentry.Maui installs the global unhandled-exception + crash capture
            // the app otherwise lacked. See docs/plans/PRINTER-APP-FLEET-OBSERVABILITY-PLAN.md.
            var sentryDsn = typeof(MauiProgram).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SentryDsn")?.Value;
            // A malformed DSN (owner typo / build-injection glitch) must DISABLE Sentry, never crash
            // the app: Sentry's MAUI initializer runs deferred inside builder.Build() (can't be caught
            // here) and throws on a bad DSN. Uri.TryCreate alone is too weak — Sentry also requires an
            // http(s) scheme, a public key (URI userinfo) and a non-root project-id path, so validate
            // the full DSN shape and stay inert if any part is missing.
            var sentryEnabled = !string.IsNullOrWhiteSpace(sentryDsn)
                && Uri.TryCreate(sentryDsn, UriKind.Absolute, out var dsnUri)
                && (dsnUri.Scheme == Uri.UriSchemeHttp || dsnUri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(dsnUri.UserInfo)
                && dsnUri.AbsolutePath.Trim('/').Length > 0;
            if (sentryEnabled)
            {
                builder.UseSentry(options =>
                {
                    options.Dsn = sentryDsn;
                    options.AutoSessionTracking = true;
                    // Never let customer PII or the API key leave the device (SECURITY-AUDIT H5/M1):
                    // no default PII; telemetry code whitelists non-PII fields only.
                    options.SendDefaultPii = false;
                });
            }

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            // Register services
            builder.Services.AddSingleton<IDeviceIdentityService, MauiDeviceIdentityService>();
            builder.Services.AddSingleton<IAppDataPathProvider, MauiAppDataPathProvider>();
            builder.Services.AddSingleton<ISecretStore, SecureStorageSecretStore>();
            builder.Services.AddSingleton<IPrinterConfigurationStore, PrinterConfigurationStore>();
            builder.Services.AddSingleton<IPrinterService, WindowsPrinterService>();
            builder.Services.AddSingleton<IRequestLogService, RequestLogService>();
            builder.Services.AddSingleton<IEventStreamingService, EventStreamingService>();
            builder.Services.AddSingleton<IOrderPrintService, OrderPrintService>();
            builder.Services.AddSingleton<IOrderHistoryService, OrderHistoryService>();
            builder.Services.AddSingleton<IUpdateService, UpdateService>();
            builder.Services.AddSingleton<IPrinterTestService, PrinterTestService>();

            // Fleet telemetry: a single long-lived HttpClient (owned here — the app doesn't reference
            // Microsoft.Extensions.Http) + the heartbeat scheduler. See the fleet-observability plan.
            builder.Services.AddSingleton<ITelemetryClient>(sp => new TelemetryClient(
                new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
                sp.GetRequiredService<ILogger<TelemetryClient>>()));
            builder.Services.AddSingleton<ITelemetryScheduler, TelemetryScheduler>();

            // Register pages
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddSingleton<OrderManagementPage>();
            builder.Services.AddSingleton<DiagnosticsPage>();
            builder.Services.AddTransient<UpdaterWindow>();

            // Sentry scope tags (device id, platform, version, tenant slug) are set from MainPage once
            // the app is up + config has loaded, not here — see IDeviceIdentityService.ApplySentryTags.
            return builder.Build();
        }
    }
}
