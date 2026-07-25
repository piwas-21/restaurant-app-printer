namespace PrinterAPP.Models;

/// <summary>
/// The order feed's position, persisted across process restarts.
/// <para><see cref="ProcessedOrders"/> is the duplicate-print guard and is authoritative;
/// <see cref="LastPollTime"/> only narrows how far back the next fetch reaches. Keeping the two
/// together means a restart can safely re-fetch a window it may have already seen: anything already
/// in <see cref="ProcessedOrders"/> is skipped rather than printed twice.</para>
/// </summary>
public class FeedCursor
{
    /// <summary>UTC timestamp passed as <c>modifiedSince</c> on the next poll.</summary>
    public DateTime LastPollTime { get; set; }

    /// <summary>Order key → UTC time it was processed. Aged out on the feed's dedup window.</summary>
    public Dictionary<string, DateTime> ProcessedOrders { get; set; } = new();
}
