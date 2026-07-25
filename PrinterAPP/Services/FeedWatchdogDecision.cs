namespace PrinterAPP.Services;

/// <summary>What the watchdog should do about the feed on this tick.</summary>
public enum FeedWatchdogAction
{
    /// <summary>Healthy, or deliberately stopped — leave it alone.</summary>
    None,

    /// <summary>Not listening at all; start it.</summary>
    Start,

    /// <summary>Listening but not producing polls; stop and start it.</summary>
    Restart,
}

/// <summary>
/// The watchdog's decision, as a pure function so it can be unit-tested without the pipeline's eight
/// collaborators (and without MAUI or Sentry) — the same "extract the decidable part" shape as
/// <see cref="PrinterTransportResolver"/> and <see cref="OrderFeedParser"/>.
/// <para>Auto-restarting a live restaurant's order feed is the kind of logic that must not misfire,
/// and the case that matters most is the one that is easiest to get wrong: never undoing a stop a
/// person actually asked for.</para>
/// </summary>
public static class FeedWatchdogDecision
{
    /// <param name="lastProgressAt">
    /// The last successful poll, or — when there has not been one yet — the moment the feed was
    /// started. Collapsing the two here is what gives a freshly started feed its grace period: a
    /// feed that has simply not had time to complete its first 5s poll must not be treated as
    /// stalled and restarted in a loop.
    /// </param>
    public static FeedWatchdogAction Decide(
        bool stoppedOnPurpose,
        bool isListening,
        DateTime lastProgressAt,
        DateTime utcNow,
        TimeSpan staleThreshold)
    {
        // A deliberate stop outranks everything. The pipeline cannot infer this from configuration:
        // on Android a configured ApiBaseUrl counts as intent-to-listen, which is right at launch and
        // wrong here — it would restart the feed seconds after somebody tapped Stop.
        if (stoppedOnPurpose)
        {
            return FeedWatchdogAction.None;
        }

        if (!isListening)
        {
            return FeedWatchdogAction.Start;
        }

        return utcNow - lastProgressAt >= staleThreshold
            ? FeedWatchdogAction.Restart
            : FeedWatchdogAction.None;
    }
}
