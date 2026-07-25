using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// The watchdog auto-restarts a live restaurant's order feed, so its decision rules are pinned here.
/// The one that matters most is the negative case: it must never undo a stop a person actually
/// asked for. On Android the pipeline cannot infer that from configuration — a configured
/// ApiBaseUrl counts as intent-to-listen at launch — so the deliberate-stop flag is the only signal,
/// and a regression there would restart the feed seconds after somebody tapped Stop.
/// </summary>
public class FeedWatchdogDecisionTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(3);
    private static readonly DateTime Now = new(2026, 7, 25, 20, 0, 0, DateTimeKind.Utc);

    private static FeedWatchdogAction Decide(
        bool stoppedOnPurpose, bool isListening, DateTime lastProgressAt) =>
        FeedWatchdogDecision.Decide(stoppedOnPurpose, isListening, lastProgressAt, Now, Threshold);

    [Theory]
    [InlineData(true)]   // stopped while it happened to be mid-poll
    [InlineData(false)]  // stopped and already quiet
    public void A_deliberate_stop_is_never_undone(bool isListening)
    {
        var action = Decide(stoppedOnPurpose: true, isListening, Now.AddHours(-2));

        Assert.Equal(FeedWatchdogAction.None, action);
    }

    [Fact]
    public void A_feed_that_is_not_listening_is_started()
    {
        var action = Decide(stoppedOnPurpose: false, isListening: false, Now);

        Assert.Equal(FeedWatchdogAction.Start, action);
    }

    [Fact]
    public void A_healthy_feed_is_left_alone()
    {
        var action = Decide(stoppedOnPurpose: false, isListening: true, Now.AddSeconds(-10));

        Assert.Equal(FeedWatchdogAction.None, action);
    }

    // "Listening but not polling" is precisely the state IsListening alone cannot express, and the
    // reason LastSuccessfulPollAt exists.
    [Fact]
    public void A_listening_feed_with_no_recent_progress_is_restarted()
    {
        var action = Decide(stoppedOnPurpose: false, isListening: true, Now.AddMinutes(-10));

        Assert.Equal(FeedWatchdogAction.Restart, action);
    }

    [Fact]
    public void Progress_exactly_at_the_threshold_counts_as_stalled()
    {
        var action = Decide(stoppedOnPurpose: false, isListening: true, Now - Threshold);

        Assert.Equal(FeedWatchdogAction.Restart, action);
    }

    // The caller passes the feed's start time when no poll has completed yet. A feed that has simply
    // not had time for its first 5s poll must not be restarted, or a slow start becomes a loop.
    [Fact]
    public void A_just_started_feed_is_given_its_grace_period()
    {
        var action = Decide(stoppedOnPurpose: false, isListening: true, Now.AddSeconds(-2));

        Assert.Equal(FeedWatchdogAction.None, action);
    }
}
