using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// The watchdog auto-restarts a live restaurant's order feed, so its rules are pinned here. Every
/// way it can misfire is a negative case: restarting something a person stopped, starting something
/// that was never meant to run, or hammering restart once a minute through a backend outage.
/// </summary>
public class FeedWatchdogDecisionTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(3);
    private static readonly DateTime Now = new(2026, 7, 25, 20, 0, 0, DateTimeKind.Utc);

    private static FeedWatchdogAction Decide(
        bool isListening,
        DateTime? lastSuccessfulPollAt,
        DateTime feedStartedAt,
        bool stoppedOnPurpose = false,
        bool shouldBeListening = true) =>
        FeedWatchdogDecision.Decide(
            stoppedOnPurpose, shouldBeListening, isListening,
            lastSuccessfulPollAt, feedStartedAt, Now, Threshold);

    [Theory]
    [InlineData(true)]   // stopped while it happened to be mid-poll
    [InlineData(false)]  // stopped and already quiet
    public void A_deliberate_stop_is_never_undone(bool isListening)
    {
        var action = Decide(isListening, Now.AddHours(-2), Now.AddHours(-2), stoppedOnPurpose: true);

        Assert.Equal(FeedWatchdogAction.None, action);
    }

    // On Windows the operator's stop intent is persisted in IsServiceRunning and outlives the
    // session-scoped stoppedOnPurpose flag. Without consulting it, the watchdog would start a feed
    // the operator had deliberately left off on the previous run — and the workstation would begin
    // printing on its own. The same guard covers a device with no API URL configured, which would
    // otherwise be "restarted" every 60s forever.
    [Fact]
    public void A_feed_that_is_not_supposed_to_run_is_left_alone()
    {
        var action = Decide(isListening: false, null, Now, shouldBeListening: false);

        Assert.Equal(FeedWatchdogAction.None, action);
    }

    [Fact]
    public void A_feed_that_should_be_listening_but_is_not_gets_started()
    {
        var action = Decide(isListening: false, null, Now);

        Assert.Equal(FeedWatchdogAction.Start, action);
    }

    [Fact]
    public void A_healthy_feed_is_left_alone()
    {
        var action = Decide(isListening: true, Now.AddSeconds(-10), Now.AddHours(-4));

        Assert.Equal(FeedWatchdogAction.None, action);
    }

    // "Listening but not polling" is precisely the state IsListening alone cannot express, and the
    // reason LastSuccessfulPollAt exists.
    [Fact]
    public void A_listening_feed_with_no_recent_progress_is_restarted()
    {
        var action = Decide(isListening: true, Now.AddMinutes(-10), Now.AddMinutes(-20));

        Assert.Equal(FeedWatchdogAction.Restart, action);
    }

    [Fact]
    public void Progress_exactly_at_the_threshold_counts_as_stalled()
    {
        var action = Decide(isListening: true, Now - Threshold, Now.AddHours(-1));

        Assert.Equal(FeedWatchdogAction.Restart, action);
    }

    // A feed that has never completed a poll is judged from when it started, so it gets a full
    // threshold to produce one rather than being restarted on the first tick.
    [Fact]
    public void A_feed_that_has_never_polled_is_judged_from_when_it_started()
    {
        Assert.Equal(FeedWatchdogAction.None, Decide(isListening: true, null, Now.AddSeconds(-2)));
        Assert.Equal(FeedWatchdogAction.Restart, Decide(isListening: true, null, Now.AddMinutes(-5)));
    }

    // The regression that matters most for noise: LastSuccessfulPollAt is never reset by stop/start,
    // so after a watchdog restart it still holds the old pre-outage timestamp. Judging on that alone
    // would mark the feed stalled again on the very next tick and restart it every 60s for the whole
    // outage — a Sentry event per minute per device, and pointless feed churn. Counting the restart
    // itself as progress is what stops that.
    [Fact]
    public void A_restart_resets_the_stall_clock_even_though_the_last_poll_is_old()
    {
        var action = Decide(
            isListening: true,
            lastSuccessfulPollAt: Now.AddHours(-1),  // backend down since then
            feedStartedAt: Now.AddSeconds(-30));     // just restarted by the previous tick

        Assert.Equal(FeedWatchdogAction.None, action);
    }

    [Fact]
    public void A_restart_that_still_produces_nothing_is_eventually_restarted_again()
    {
        var action = Decide(
            isListening: true,
            lastSuccessfulPollAt: Now.AddHours(-1),
            feedStartedAt: Now.AddMinutes(-4));

        Assert.Equal(FeedWatchdogAction.Restart, action);
    }
}
