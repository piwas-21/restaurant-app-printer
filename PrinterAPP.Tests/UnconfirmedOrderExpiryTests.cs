using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// An unconfirmed order holds the persisted cursor back so a restart re-fetches and reprints it.
/// These pin when that protection ends — and, just as importantly, the ordering of the three coupled
/// timings it depends on, which CLAUDE.md documents as an invariant but which nothing enforced.
/// </summary>
public class UnconfirmedOrderExpiryTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(25);

    [Fact]
    public void An_order_inside_the_retention_window_is_kept()
    {
        var windows = new Dictionary<string, DateTime> { ["ORD-1"] = Now.AddMinutes(-24) };

        Assert.Empty(UnconfirmedOrderExpiry.Expired(windows, Now, Retention));
    }

    [Fact]
    public void An_order_past_the_retention_window_is_expired()
    {
        var windows = new Dictionary<string, DateTime> { ["ORD-1"] = Now.AddMinutes(-26) };

        Assert.Equal(new[] { "ORD-1" }, UnconfirmedOrderExpiry.Expired(windows, Now, Retention));
    }

    [Fact]
    public void Only_the_orders_past_the_window_are_expired()
    {
        var windows = new Dictionary<string, DateTime>
        {
            ["old"] = Now.AddMinutes(-40),
            ["fresh"] = Now.AddMinutes(-1),
            ["borderline"] = Now - Retention,
        };

        var expired = UnconfirmedOrderExpiry.Expired(windows, Now, Retention);

        Assert.Equal(new[] { "old" }, expired);
        // Exactly at the boundary is NOT expired: while it is still within the look-back clamp its
        // cursor floor survives a restore, so the order is still recoverable.
        Assert.DoesNotContain("borderline", expired);
    }

    [Fact]
    public void The_result_can_be_iterated_while_the_source_is_mutated()
    {
        var windows = new Dictionary<string, DateTime> { ["ORD-1"] = Now.AddMinutes(-40) };

        // The caller removes from the same dictionary it passed in; a lazily-evaluated result would
        // throw here.
        var exception = Record.Exception(() =>
        {
            foreach (var key in UnconfirmedOrderExpiry.Expired(windows, Now, Retention))
            {
                windows.Remove(key);
            }
        });

        Assert.Null(exception);
        Assert.Empty(windows);
    }

    /// <summary>
    /// The invariant the whole restart guarantee rests on, asserted rather than merely written down.
    /// <para>Unconfirmed retention must sit INSIDE the cursor look-back clamp: past it, an
    /// unconfirmed order's cursor floor is discarded by the very load it exists to influence, so the
    /// order is neither re-fetched nor reported — a silently dropped ticket. And the clamp must sit
    /// inside the dedup window, or a re-fetch reaches orders no surviving dedup entry guards, which
    /// reprints them. Getting either ordering wrong fails silently in production, which is exactly
    /// why it is pinned here.</para>
    /// </summary>
    [Fact]
    public void The_three_coupled_timings_stay_correctly_ordered()
    {
        Assert.True(
            EventStreamingService.UnconfirmedRetention < FeedCursorStore.MaxLookBack,
            $"unconfirmed retention ({EventStreamingService.UnconfirmedRetention}) must be inside the "
            + $"cursor look-back clamp ({FeedCursorStore.MaxLookBack}), or an unconfirmed order's "
            + "cursor floor is discarded on load and the ticket is silently lost.");

        Assert.True(
            FeedCursorStore.MaxLookBack < EventStreamingService.DedupWindow,
            $"the cursor look-back clamp ({FeedCursorStore.MaxLookBack}) must be inside the dedup "
            + $"window ({EventStreamingService.DedupWindow}), or a re-fetch reaches orders no "
            + "surviving dedup entry guards and they reprint.");

        // A negative retention would expire every order on its first cleanup and flood the operator
        // with false "may not have printed" warnings — the failure mode if these two ever end up in
        // a static-initialisation cycle.
        Assert.True(EventStreamingService.UnconfirmedRetention > TimeSpan.Zero);
    }
}
