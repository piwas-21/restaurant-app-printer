using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IUpdateService
{
    string GetCurrentVersion();
    Task<UpdateInfo> CheckForUpdateAsync();
    Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<int>? progress = null);
}
