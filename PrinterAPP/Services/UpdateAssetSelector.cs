using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Which platform's artifact an update check should look for.</summary>
public enum UpdatePlatform
{
    Android,
    Windows
}

/// <summary>
/// Picks the release asset that belongs to the running platform, as a pure function over the
/// release's asset list — the same "extract the decidable part" shape as
/// <see cref="FeedWatchdogDecision"/> and <see cref="KitchenTicketFilter"/>.
/// <para><b>Why the platform is a parameter and not an <c>#if ANDROID</c>:</b> until v1.0.20 this
/// selection lived inline in <c>UpdateService</c> with no Android branch at all, so an Android
/// device evaluated the Windows arm — <c>Environment.Is64BitOperatingSystem</c> is true on arm64, so
/// it matched <c>PrinterApp-Setup-x64.exe</c> and every Android "Update Now" downloaded a 300&#160;MB
/// Windows installer before failing in the batch-script install path. A compile-time branch is
/// invisible to CI (the test project is plain <c>net10.0</c>), so the defect could not be seen by any
/// gate. Taking the platform as an argument makes BOTH arms testable on any host.</para>
/// </summary>
public static class UpdateAssetSelector
{
    /// <summary>
    /// The asset to download, or <c>null</c> when the release carries nothing for this platform —
    /// which the caller must treat as "no update offered", never as a reason to fall back to another
    /// platform's artifact.
    /// </summary>
    /// <param name="assets">The release's attached files.</param>
    /// <param name="platform">The platform the app is running on.</param>
    /// <param name="is64Bit">Windows only: selects the x64 vs x86 installer. Ignored on Android.</param>
    public static GitHubAsset? Select(IEnumerable<GitHubAsset>? assets, UpdatePlatform platform, bool is64Bit)
    {
        if (assets is null)
            return null;

        var candidates = assets.ToList();

        if (platform == UpdatePlatform.Android)
        {
            // No arch fallback and no cross-platform fallback: an Android build that finds no .apk
            // has nothing it can install.
            return candidates.FirstOrDefault(a => HasExtension(a, ".apk"));
        }

        var arch = is64Bit ? "x64" : "x86";
        return candidates.FirstOrDefault(a => HasExtension(a, ".exe") && Contains(a, arch))
               // Legacy releases published a single un-suffixed installer.
               ?? candidates.FirstOrDefault(a => HasExtension(a, ".exe"));
    }

    // Both matchers are null-safe rather than guarded by a prior filter: GitHub declares `name`
    // nullable, and a name we cannot read simply matches nothing.
    private static bool HasExtension(GitHubAsset asset, string extension) =>
        asset.Name?.EndsWith(extension, StringComparison.OrdinalIgnoreCase) == true;

    private static bool Contains(GitHubAsset asset, string fragment) =>
        asset.Name?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;
}
