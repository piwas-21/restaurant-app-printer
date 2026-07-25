namespace PrinterAPP.Services;

/// <summary>What the watchdog should do about the feed on this tick.</summary>
public enum FeedWatchdogAction
{
    /// <summary>Healthy, or not supposed to be running — leave it alone.</summary>
    None,

    /// <summary>Should be listening and is not; start it.</summary>
    Start,

    /// <summary>Listening but not producing polls; stop and start it.</summary>
    Restart,
}

/// <summary>
/// The watchdog's decision, as a pure function so it can be unit-tested without the pipeline's
/// collaborators (and without MAUI or Sentry) — the same "extract the decidable part" shape as
/// <see cref="PrinterTransportResolver"/> and <see cref="OrderFeedParser"/>.
/// <para>Auto-restarting a live restaurant's order feed is logic that must not misfire, and the ways
/// it can misfire are all negative cases: restarting something a person stopped, starting something
/// that was never meant to run, or hammering restart once a minute through a backend outage. All of
/// them are decided here rather than in the timer loop, so all of them are testable.</para>
/// </summary>
public static class FeedWatchdogDecision
{
    /// <param name="stoppedOnPurpose">A person stopped the feed during this session.</param>
    /// <param name="shouldBeListening">
    /// The saved configuration says this device should be running the feed. Without it, "not
    /// listening" would be read as failure even when the operator had deliberately left it stopped
    /// (Windows persists that intent in <c>IsServiceRunning</c>, which outlives the session-scoped
    /// <paramref name="stoppedOnPurpose"/> flag) or when the device has no API URL configured at all.
    /// </param>
    /// <param name="lastSuccessfulPollAt">Null until the feed completes its first poll in this process.</param>
    /// <param name="feedStartedAt">When the current listening session began.</param>
    public static FeedWatchdogAction Decide(
        bool stoppedOnPurpose,
        bool shouldBeListening,
        bool isListening,
        DateTime? lastSuccessfulPollAt,
        DateTime feedStartedAt,
        DateTime utcNow,
        TimeSpan staleThreshold)
    {
        if (stoppedOnPurpose || !shouldBeListening)
        {
            return FeedWatchdogAction.None;
        }

        if (!isListening)
        {
            return FeedWatchdogAction.Start;
        }

        // The LATER of "when this listening session started" and "when it last succeeded".
        //
        // Taking the max is what stops a backend outage becoming a restart-every-minute loop:
        // LastSuccessfulPollAt is never reset by stop/start, so once a feed has succeeded even once,
        // a plain `lastSuccessfulPollAt ?? feedStartedAt` would keep returning that same frozen
        // timestamp after every restart and the feed would be judged stalled forever. Counting the
        // restart itself as progress gives each attempt a fresh threshold to beat.
        var lastProgressAt = lastSuccessfulPollAt is { } polled && polled > feedStartedAt
            ? polled
            : feedStartedAt;

        return utcNow - lastProgressAt >= staleThreshold
            ? FeedWatchdogAction.Restart
            : FeedWatchdogAction.None;
    }
}
