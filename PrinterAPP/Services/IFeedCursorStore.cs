using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Persists the order feed's position so a process restart does not reprint recent orders.
/// <para>Both the poll cursor and the dedup set used to live only in memory, so every restart began
/// at <c>UtcNow-30min</c> with an empty dedup set and re-printed everything confirmed in that
/// window. That was survivable while a restart meant a person relaunching the app; with the Android
/// foreground service restarting itself (START_STICKY, boot) it would put duplicate tickets on the
/// pass. See ADR-007 and the cross-platform plan, Phase 9d.</para>
/// </summary>
public interface IFeedCursorStore
{
    /// <summary>
    /// Reads the persisted cursor, or a fresh one when there is nothing readable on disk.
    /// Never throws — an unreadable cursor degrades to "start from the default window".
    /// </summary>
    FeedCursor Load();

    /// <summary>Writes the cursor. Best-effort — never throws.</summary>
    void Save(FeedCursor cursor);
}
