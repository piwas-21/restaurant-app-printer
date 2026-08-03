using System.Text.Json.Serialization;

namespace PrinterAPP.Models;

/// <summary>
/// The subset of GitHub's "get latest release" response the updater reads, from the public
/// <c>piwas-21/printer-app-releases</c> repo. Mirrors the GitHub REST API, NOT a RUMI backend DTO —
/// the §3 backend-contract rule does not apply here.
/// <para>Public (rather than nested private in <c>UpdateService</c>) so
/// <see cref="Services.UpdateAssetSelector"/> can be unit-tested against real release payloads
/// without a MAUI host.</para>
/// </summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

/// <summary>One downloadable file attached to a release (an installer <c>.exe</c> or the <c>.apk</c>).</summary>
public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
