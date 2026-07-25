using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
using PrinterAPP.Services;
using Sentry;

namespace PrinterAPP;

/// <summary>
/// Keeps the order feed polling and printing while the app is not the foreground UI.
/// <para>Without this the whole order path lived in a normal backgrounded process, which Android
/// throttles (Doze / App Standby suspend network and defer timers) and then reclaims under memory
/// pressure — silently, with no crash to report. That is the defect this service fixes: printing
/// used to stop the moment staff pressed Home or opened another app.</para>
/// <para>Declared with the <c>specialUse</c> foreground-service type. <c>dataSync</c> — the obvious
/// alternative — is capped at 6 hours per day on Android 15+, which would kill the service partway
/// through a restaurant's service day. See docs/adr/ADR-007-android-foreground-service.md.</para>
/// </summary>
[Service(
    Name = "com.companyname.printerapp.OrderFeedForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class OrderFeedForegroundService : Service
{
    private const string ChannelId = "sofra_printer_order_feed";
    private const int NotificationId = 1001;

    // The notification doubles as the staff-visible health indicator (and the only one available
    // without unlocking the app), so it is refreshed on a timer rather than left stale.
    private static readonly TimeSpan NotificationRefreshInterval = TimeSpan.FromSeconds(30);

    private PowerManager.WakeLock? _wakeLock;
    private CancellationTokenSource? _cts;
    private IOrderPipeline? _pipeline;
    private IEventStreamingService? _feed;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
        AcquireWakeLock();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartInForeground();

        // MauiApplication.OnCreate builds the DI container before any component starts, including a
        // service started from BOOT_COMPLETED, so this should always resolve. If it somehow does not,
        // stay up rather than dying silently — the next start command retries.
        var services = IPlatformApplication.Current?.Services;
        if (services is not null)
        {
            _pipeline ??= services.GetService<IOrderPipeline>();
            _feed ??= services.GetService<IEventStreamingService>();
        }

        if (_cts is null)
        {
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }

        // Sticky: if Android reclaims the process under memory pressure it recreates the service as
        // soon as resources allow. That is the difference between "the tablet was busy for a minute"
        // and "orders stopped printing until somebody noticed".
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        ReleaseWakeLock();
        base.OnDestroy();
    }

    /// <summary>
    /// Android may kill the process when the user swipes the app off the recents list. Restart the
    /// service so an accidental swipe does not silently stop order printing — an intentional stop is
    /// the in-app toggle or a force-stop, both of which are honoured.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        using var restart = new Intent(ApplicationContext, typeof(OrderFeedForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            ApplicationContext?.StartForegroundService(restart);
        }
        else
        {
            ApplicationContext?.StartService(restart);
        }

        base.OnTaskRemoved(rootIntent);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_pipeline is not null)
            {
                await _pipeline.InitializeAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // A failed start is precisely the class of failure the fleet dashboard exists to catch.
            SentrySdk.CaptureException(ex);
        }

        using var timer = new PeriodicTimer(NotificationRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                UpdateNotification();
            }
        }
        catch (System.OperationCanceledException)
        {
            // Expected on OnDestroy.
        }
    }

    private void StartInForeground()
    {
        var notification = BuildNotification();

        // Android 14+ requires the type at promotion time as well as in the manifest, or the platform
        // throws MissingForegroundServiceTypeException. OperatingSystem.IsAndroidVersionAtLeast is
        // used rather than Build.VERSION.SdkInt because the platform-compatibility analyser
        // understands it, so the API-level guard is verified at compile time.
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void UpdateNotification()
    {
        var manager = NotificationManagerCompat.From(this);
        manager.Notify(NotificationId, BuildNotification());
    }

    private Notification BuildNotification()
    {
        using var launch = new Intent(this, typeof(MainActivity));
        launch.SetFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask);

        // Immutable is mandatory from API 31; harmless below it.
        var contentIntent = PendingIntent.GetActivity(
            this, 0, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("SP-PrinterApp")
            .SetContentText(DescribeState())
            // A framework drawable, not the app icon: notification small icons are rendered as an
            // alpha mask, so a full-colour mipmap shows up as a white blob.
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifySync)
            .SetContentIntent(contentIntent)
            .SetOngoing(true)
            .SetPriority((int)NotificationPriority.Low)
            .SetShowWhen(false)
            .Build()!;
    }

    private string DescribeState()
    {
        if (_feed is null)
        {
            return "Starting…";
        }

        if (!_feed.IsListening)
        {
            return "Order feed stopped — open the app to start it";
        }

        var lastPoll = _feed.LastSuccessfulPollAt;
        return lastPoll is null
            ? "Connecting to the order feed…"
            : $"Listening for orders — last check {lastPoll.Value.ToLocalTime():HH:mm:ss}";
    }

    private void CreateNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        // Low importance: the notification is a permanent status indicator, so it must never make a
        // sound or interrupt staff mid-service.
        var channel = new NotificationChannel(
            ChannelId, "Order feed", NotificationImportance.Low)
        {
            Description = "Shows that the app is listening for orders and able to print them.",
        };
        channel.SetShowBadge(false);

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(channel);
    }

    private void AcquireWakeLock()
    {
        // A foreground service exempts the app from background execution limits but does NOT keep
        // the SoC awake: with the screen off the CPU suspends and the feed's 5s Task.Delay stops
        // firing. These are mains-powered kitchen tablets, so a partial wake lock is the right trade.
        //
        // No Wi-Fi lock alongside it: WIFI_MODE_FULL_HIGH_PERF has been deprecated since API 29 and
        // is a no-op on modern Android. With the CPU held awake the radio stays associated and our
        // outgoing polls keep it up, so the wake lock is what actually carries the guarantee.
        var power = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "SofraPrinter::OrderFeed");
        _wakeLock?.Acquire();
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }

        _wakeLock?.Dispose();
        _wakeLock = null;
    }
}
