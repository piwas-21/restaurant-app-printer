using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IUpdateService
{
    string GetCurrentVersion();
    Task<UpdateInfo> CheckForUpdateAsync();

    /// <summary>
    /// Downloads the release artifact for this platform and hands it to the installer. The outcome
    /// distinguishes "installer launched" from "blocked on a permission" from "failed" — a bool
    /// could not, which is why a blocked Android install used to report success and do nothing.
    /// </summary>
    Task<UpdateInstallOutcome> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<int>? progress = null);
}
