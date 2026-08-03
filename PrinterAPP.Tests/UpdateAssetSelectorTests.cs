using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Pins which artifact each platform downloads.
/// <para>This is a regression suite for a defect that reached a customer device: builds before
/// v1.0.20 had no Android arm at all, so an Android tablet evaluated the Windows one. Because
/// <c>Environment.Is64BitOperatingSystem</c> is true on arm64, "x64" matched and the phone downloaded
/// <c>PrinterApp-Setup-x64.exe</c> — a ~300&#160;MB Windows installer — then failed in the batch-script
/// install path with "Update failed. Please try again or download manually." The GitHub download
/// counters on the v1.0.25 release recorded it: 1 for the x64 .exe, 0 for the .apk.</para>
/// <para>The asset names below are the real ones published by <c>build-release.yml</c>.</para>
/// </summary>
public class UpdateAssetSelectorTests
{
    private static GitHubAsset Asset(string name) =>
        new() { Name = name, BrowserDownloadUrl = $"https://example.invalid/{name}", Size = 1024 };

    private static List<GitHubAsset> RealReleaseAssets() =>
    [
        Asset("PrinterApp-Android.apk"),
        Asset("PrinterApp-Setup-x64.exe"),
        Asset("PrinterApp-Setup-x86.exe")
    ];

    // The exact production failure. is64Bit is true on a 64-bit Android device, which is what made
    // the Windows arm match — so it is pinned true here rather than defaulted away.
    [Fact]
    public void Android_takes_the_apk_and_never_a_windows_installer()
    {
        var asset = UpdateAssetSelector.Select(RealReleaseAssets(), UpdatePlatform.Android, is64Bit: true);

        Assert.Equal("PrinterApp-Android.apk", asset?.Name);
    }

    // An Android build that finds no .apk must offer nothing. Falling back to any other asset is how
    // a phone ends up downloading a Windows installer.
    [Fact]
    public void Android_selects_nothing_when_a_release_ships_no_apk()
    {
        var windowsOnly = new List<GitHubAsset>
        {
            Asset("PrinterApp-Setup-x64.exe"),
            Asset("PrinterApp-Setup-x86.exe")
        };

        var asset = UpdateAssetSelector.Select(windowsOnly, UpdatePlatform.Android, is64Bit: true);

        Assert.Null(asset);
    }

    [Theory]
    [InlineData(true, "PrinterApp-Setup-x64.exe")]
    [InlineData(false, "PrinterApp-Setup-x86.exe")]
    public void Windows_takes_the_installer_for_its_architecture(bool is64Bit, string expected)
    {
        var asset = UpdateAssetSelector.Select(RealReleaseAssets(), UpdatePlatform.Windows, is64Bit);

        Assert.Equal(expected, asset?.Name);
    }

    // The mirror of the Android case: a Windows host must not install the .apk.
    [Fact]
    public void Windows_selects_nothing_when_a_release_ships_only_an_apk()
    {
        var androidOnly = new List<GitHubAsset> { Asset("PrinterApp-Android.apk") };

        var asset = UpdateAssetSelector.Select(androidOnly, UpdatePlatform.Windows, is64Bit: true);

        Assert.Null(asset);
    }

    // Releases before the x64/x86 split published one un-suffixed installer; those installs still poll.
    [Fact]
    public void Windows_falls_back_to_an_un_suffixed_installer()
    {
        var legacy = new List<GitHubAsset> { Asset("PrinterApp-Setup.exe") };

        var asset = UpdateAssetSelector.Select(legacy, UpdatePlatform.Windows, is64Bit: true);

        Assert.Equal("PrinterApp-Setup.exe", asset?.Name);
    }

    [Fact]
    public void A_release_with_no_assets_selects_nothing()
    {
        Assert.Null(UpdateAssetSelector.Select(null, UpdatePlatform.Android, is64Bit: true));
        Assert.Null(UpdateAssetSelector.Select([], UpdatePlatform.Windows, is64Bit: true));
    }

    // GitHub returns the field, so a null/blank name is possible; it must not throw mid-check, which
    // CheckForUpdateAsync would swallow into a silent "you have the latest version".
    [Fact]
    public void An_asset_with_no_name_is_skipped_rather_than_throwing()
    {
        var assets = new List<GitHubAsset>
        {
            new() { Name = null, BrowserDownloadUrl = "https://example.invalid/x", Size = 1 },
            new() { Name = "   ", BrowserDownloadUrl = "https://example.invalid/y", Size = 1 },
            Asset("PrinterApp-Android.apk")
        };

        var asset = UpdateAssetSelector.Select(assets, UpdatePlatform.Android, is64Bit: true);

        Assert.Equal("PrinterApp-Android.apk", asset?.Name);
    }
}
