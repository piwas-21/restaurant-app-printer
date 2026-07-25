using Android.App;
using Android.Content;
using Android.OS;

namespace PrinterAPP;

/// <summary>
/// Restarts <see cref="OrderFeedForegroundService"/> after a device reboot, so a tablet that was
/// power-cycled overnight (or by a power cut) comes back printing without anyone opening the app.
/// <para><c>BOOT_COMPLETED</c> is one of the few exemptions from the Android 12+ ban on starting a
/// foreground service from the background. Note the platform's stopped-state rule: the app must have
/// been launched at least once since install for this broadcast to be delivered.</para>
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, QuickBootPowerOn })]
public class BootReceiver : BroadcastReceiver
{
    // Several OEM ROMs send this instead of (or as well as) ACTION_BOOT_COMPLETED after a fast boot.
    private const string QuickBootPowerOn = "android.intent.action.QUICKBOOT_POWERON";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action is not (Intent.ActionBootCompleted or QuickBootPowerOn))
        {
            return;
        }

        using var serviceIntent = new Intent(context, typeof(OrderFeedForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            context.StartForegroundService(serviceIntent);
        }
        else
        {
            context.StartService(serviceIntent);
        }
    }
}
