using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Whether a restaurant is offered an update at all. Every failure here is silent — the device
/// simply reports "You have the latest version!" and stays on a stale build — so the unparseable
/// cases matter as much as the ordinary ones.
/// </summary>
public class ReleaseVersionTests
{
    // The real pairing on the device that prompted this work.
    [Fact]
    public void An_older_install_is_offered_a_newer_release()
    {
        Assert.True(ReleaseVersion.IsNewer("1.0.16", "1.0.25"));
    }

    [Theory]
    [InlineData("1.0.25", "1.0.25")]  // same build
    [InlineData("1.0.25", "1.0.24")]  // release repo behind the installed build
    [InlineData("1.1.0", "1.0.99")]   // minor beats a high patch
    [InlineData("2.0.0", "1.9.9")]
    public void A_release_that_is_not_ahead_is_not_offered(string current, string latest)
    {
        Assert.False(ReleaseVersion.IsNewer(current, latest));
    }

    // Numeric, not lexicographic: "1.0.9" vs "1.0.10" is where a string compare silently inverts.
    [Fact]
    public void Parts_compare_numerically_not_as_text()
    {
        Assert.True(ReleaseVersion.IsNewer("1.0.9", "1.0.10"));
        Assert.False(ReleaseVersion.IsNewer("1.0.10", "1.0.9"));
    }

    // Tags carry a leading "v"; GetCurrentVersion() does not.
    [Fact]
    public void A_leading_v_is_accepted_on_either_side()
    {
        Assert.True(ReleaseVersion.IsNewer("1.0.16", "v1.0.25"));
        Assert.False(ReleaseVersion.IsNewer("v1.0.25", "1.0.25"));
    }

    [Fact]
    public void Missing_trailing_parts_count_as_zero()
    {
        Assert.Equal(0, ReleaseVersion.Compare("1.0", "1.0.0"));
        Assert.True(ReleaseVersion.IsNewer("1.0", "1.0.1"));
    }

    // The previous implementation used int.Parse on each part, so ANY of these threw inside
    // CheckForUpdateAsync's catch-all and the device reported "latest version" forever. Parsing must
    // degrade, never throw.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("next")]
    [InlineData("1.0.beta")]
    [InlineData("...")]
    [InlineData("1.0.25.3.7")]
    public void A_malformed_version_never_throws(string? malformed)
    {
        var exception = Record.Exception(() =>
        {
            ReleaseVersion.IsNewer("1.0.16", malformed);
            ReleaseVersion.IsNewer(malformed, "1.0.25");
        });

        Assert.Null(exception);
    }

    // Fail closed: a release we cannot read is not offered, rather than offered wrongly.
    [Theory]
    [InlineData("next")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unreadable_release_tag_is_not_offered(string? tag)
    {
        Assert.False(ReleaseVersion.IsNewer("1.0.16", tag));
    }

    // A pre-release suffix reads its numeric prefix, so v1.1.0-rc1 still sorts above 1.0.25.
    [Fact]
    public void A_prerelease_suffix_reads_its_numeric_prefix()
    {
        Assert.True(ReleaseVersion.IsNewer("1.0.25", "v1.1.0-rc1"));
        Assert.Equal(0, ReleaseVersion.Compare("1.0.25", "1.0.25-rc1"));
    }
}
