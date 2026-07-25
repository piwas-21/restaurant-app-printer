namespace PrinterAPP.Services;

/// <summary>
/// Which dispatched-but-unconfirmed orders can no longer be recovered, as a pure function — the same
/// "extract the decidable part" shape as <see cref="FeedWatchdogDecision"/> and
/// <see cref="OrderFeedParser"/>.
/// <para>An unconfirmed order holds the persisted cursor back so a restart re-fetches and reprints
/// it. That only works while the floored cursor survives <see cref="FeedCursorStore.MaxLookBack"/>,
/// which clamps any restored cursor forward — so the retention window has to sit inside the clamp.
/// Past it the order is genuinely unrecoverable and the operator has to be told, rather than
/// discovering a missing ticket from a customer.</para>
/// </summary>
public static class UnconfirmedOrderExpiry
{
    /// <summary>
    /// Keys whose poll window is older than <paramref name="retention"/>. Materialised rather than
    /// lazy so the caller can mutate the source dictionary while iterating the result.
    /// </summary>
    public static List<string> Expired(
        IReadOnlyDictionary<string, DateTime> unconfirmedPollWindows,
        DateTime utcNow,
        TimeSpan retention)
    {
        var cutoff = utcNow - retention;
        var expired = new List<string>();

        foreach (var (orderNumber, pollWindow) in unconfirmedPollWindows)
        {
            if (pollWindow < cutoff)
            {
                expired.Add(orderNumber);
            }
        }

        return expired;
    }
}
