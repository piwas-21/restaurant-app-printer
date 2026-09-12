namespace PrinterAPP.Models;

/// <summary>
/// The order and additive-update feed positions, persisted across process restarts. The update
/// cursor is opaque: only the backend can interpret its timestamp/job-id pair.
/// </summary>
public class FeedCursor
{
    /// <summary>UTC timestamp passed as <c>modifiedSince</c> on the next order poll.</summary>
    public DateTime LastPollTime { get; set; }

    /// <summary>Order key → UTC time it was processed.</summary>
    public Dictionary<string, DateTime> ProcessedOrders { get; set; } = new();

    /// <summary>Opaque <c>nextUpdateCursor</c> returned by the update feed, including terminal pages.</summary>
    public string? LastUpdateCursor { get; set; }
}
