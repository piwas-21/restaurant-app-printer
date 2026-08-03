namespace PrinterAPP.Services;

/// <summary>
/// Compares the running app's version against a GitHub release tag, as a pure function.
/// <para>Extracted from <c>UpdateService</c> because it decides whether a restaurant is offered an
/// update at all, and it is the one step whose failure mode is silence: the previous implementation
/// used <c>int.Parse</c> on each dot-separated part, so a single non-numeric tag threw inside
/// <c>CheckForUpdateAsync</c>'s catch-all and every device reported "You have the latest version!"
/// forever. Parsing here never throws — an unreadable version compares as older, so an unreadable
/// *release* is simply not offered rather than being offered wrongly.</para>
/// </summary>
public static class ReleaseVersion
{
    /// <summary>Version parts beyond the fourth are ignored, matching <see cref="System.Version"/>.</summary>
    private const int MaxParts = 4;

    /// <summary>
    /// True when <paramref name="latest"/> is a strictly higher version than <paramref name="current"/>.
    /// Accepts an optional leading <c>v</c> (release tags are <c>v1.0.25</c>) and tolerates a
    /// pre-release suffix on a part (<c>1.1.0-rc1</c> reads its numeric prefix).
    /// </summary>
    public static bool IsNewer(string? current, string? latest) => Compare(current, latest) < 0;

    /// <summary>
    /// Negative when <paramref name="a"/> precedes <paramref name="b"/>, positive when it follows,
    /// zero when they are equal. Missing trailing parts count as zero, so <c>1.0</c> equals <c>1.0.0</c>.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var left = Parse(a);
        var right = Parse(b);

        for (int i = 0; i < MaxParts; i++)
        {
            var comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    /// <summary>
    /// A fixed-width numeric view of a version string. Anything unparseable degrades to zero rather
    /// than throwing, so a malformed tag can never wedge the update check.
    /// </summary>
    private static int[] Parse(string? version)
    {
        var parts = new int[MaxParts];
        if (string.IsNullOrWhiteSpace(version))
            return parts;

        var trimmed = version.Trim().TrimStart('v', 'V');
        var segments = trimmed.Split('.');

        for (int i = 0; i < MaxParts && i < segments.Length; i++)
            parts[i] = LeadingNumber(segments[i]);

        return parts;
    }

    /// <summary>The leading digits of a segment ("25" from "25", "0" from "0-rc1", 0 from "next").</summary>
    private static int LeadingNumber(string segment)
    {
        int end = 0;
        while (end < segment.Length && char.IsAsciiDigit(segment[end]))
            end++;

        return end > 0 && int.TryParse(segment.AsSpan(0, end), out var value) ? value : 0;
    }
}
