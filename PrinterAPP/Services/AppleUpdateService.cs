using System.Reflection;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Apple builds are updated through their signing/distribution channel, never an installer.</summary>
public sealed class AppleUpdateService : IUpdateService
{
    public string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public Task<UpdateInfo> CheckForUpdateAsync() => Task.FromResult(new UpdateInfo
    {
        CurrentVersion = GetCurrentVersion(),
        ReleaseNotes = "This iOS pilot is updated by installing a newly signed build."
    });

    public Task<UpdateInstallOutcome> DownloadAndInstallUpdateAsync(
        UpdateInfo updateInfo, IProgress<int>? progress = null) =>
        Task.FromResult(UpdateInstallOutcome.Failed);
}
