using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Whether a device should be running the order feed, from its saved configuration alone.
/// <para>Consulted both at launch (<see cref="IOrderPipeline.InitializeAsync"/>) and on every
/// watchdog tick, so the two can never disagree about what "supposed to be running" means — a
/// divergence there would either leave a device silently not printing, or have the watchdog start a
/// feed the operator deliberately left stopped.</para>
/// <para>Pure and platform-conditional, hence its own file: the <c>#if WINDOWS</c> rule below is
/// exactly the sort of thing that is invisible until it misbehaves on one platform only.</para>
/// </summary>
public static class FeedStartupPolicy
{
    /// <summary>
    /// Windows keeps honouring the persisted <c>IsServiceRunning</c> flag. On Android the manual
    /// Start control was historically absent and existing installs could never persist
    /// <c>IsServiceRunning=true</c>, so a configured <c>ApiBaseUrl</c> is treated as intent to
    /// listen. <c>ApiBaseUrl</c> defaults to a non-empty value, so in practice the Android feed
    /// always comes up — the intended behaviour for an always-on printer appliance (a Stop tap lasts
    /// the session, not across restarts). A blank <c>ApiBaseUrl</c> means an unconfigured device,
    /// which must not be treated as a feed that failed.
    /// </summary>
    public static bool ShouldBeListening(PrinterConfiguration config) =>
#if WINDOWS
        config.IsServiceRunning;
#else
        config.IsServiceRunning || !string.IsNullOrWhiteSpace(config.ApiBaseUrl);
#endif
}
