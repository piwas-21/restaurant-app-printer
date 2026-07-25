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
                    // Sofra "craft" typography (branding P1): Quicksand body + Amatic SC display headings.
                    fonts.AddFont("Quicksand-Regular.ttf", "Quicksand");
                    fonts.AddFont("Quicksand-Medium.ttf", "QuicksandMedium");
                    fonts.AddFont("AmaticSC-Bold.ttf", "AmaticSC");
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
            // Persisted poll cursor + dedup window — without it every process restart re-fetches and
            // re-prints the last 30 minutes of orders (cross-platform plan, Phase 9d).
            builder.Services.AddSingleton<IFeedCursorStore, FeedCursorStore>();
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
            builder.Services.AddSingleton<IPrintAckOutbox, PrintAckOutbox>();
            builder.Services.AddSingleton<ITelemetryScheduler, TelemetryScheduler>();

            // Headless order path (feed → print → history → ack). Lives outside the UI so it keeps
            // running with no page or Activity — on Android it is driven by a foreground service, and
            // after a reboot there is no UI at all. See ADR-007.
            builder.Services.AddSingleton<IOrderPipeline, OrderPipeline>();
            // Restarts a feed that died or went quiet. Started by IBackgroundRunner, not by the
            // pipeline — it depends on IOrderPipeline, so the pipeline cannot own it (Phase 9e).
            builder.Services.AddSingleton<IFeedWatchdog, FeedWatchdog>();
#if ANDROID
            builder.Services.AddSingleton<IBackgroundRunner, AndroidBackgroundRunner>();
#else
            // Windows (and any future head): a minimised desktop process is not frozen or reclaimed,
            // so the pipeline runs in-process with no host service.
            builder.Services.AddSingleton<IBackgroundRunner, DirectBackgroundRunner>();
#endif

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
