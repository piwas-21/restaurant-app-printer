using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public class UpdateService : IUpdateService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _httpClient;
    // Public releases repo — source code is private (piwas-21/restaurant-app-printer),
    // but binaries are published here so the updater works without a GitHub account.
    // AC: piwas-21/restaurant-app-printer#22 (auto-update channel change — see CLAUDE.md §9).
    private const string GITHUB_REPO = "piwas-21/printer-app-releases";
    private const string RELEASES_API_URL = $"https://api.github.com/repos/{GITHUB_REPO}/releases/latest";

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PrinterApp-Updater");
    }

    /// <summary>The platform this build targets, as the pure selectors need it stated.</summary>
    private static UpdatePlatform CurrentPlatform =>
#if ANDROID
        UpdatePlatform.Android;
#else
        UpdatePlatform.Windows;
#endif

    public string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }

    public async Task<UpdateInfo> CheckForUpdateAsync()
    {
        var updateInfo = new UpdateInfo
        {
            CurrentVersion = GetCurrentVersion()
        };

        try
        {
            _logger.LogInformation("Checking for updates from GitHub...");

            var response = await _httpClient.GetFromJsonAsync<GitHubRelease>(RELEASES_API_URL);

            if (response == null)
            {
                _logger.LogWarning("No release information found");
                return updateInfo;
            }

            // Parse version from tag (e.g., "v1.0.1" -> "1.0.1")
            var latestVersion = response.TagName?.TrimStart('v') ?? "0.0.0";
            updateInfo.LatestVersion = latestVersion;
            updateInfo.ReleaseName = response.Name ?? "";
            updateInfo.ReleaseNotes = response.Body ?? "";

            // Pick the asset for this platform: the .apk on Android, the arch-specific .exe on
            // Windows. The platform is passed in rather than branched on here so both arms stay
            // testable off-device — see UpdateAssetSelector for what that cost us before v1.0.20.
            var asset = UpdateAssetSelector.Select(response.Assets, CurrentPlatform, Environment.Is64BitOperatingSystem);

            if (asset != null)
            {
                updateInfo.DownloadUrl = asset.BrowserDownloadUrl ?? "";
                updateInfo.FileSize = asset.Size;
            }

            // An update with no artifact for this platform is not an update we can offer: the
            // download would fail, and before v1.0.20 it silently fetched the other platform's.
            updateInfo.UpdateAvailable =
                ReleaseVersion.IsNewer(updateInfo.CurrentVersion, latestVersion) &&
                !string.IsNullOrWhiteSpace(updateInfo.DownloadUrl);

            _logger.LogInformation("Current version: {Current}, Latest version: {Latest}, Update available: {Available}",
                updateInfo.CurrentVersion, latestVersion, updateInfo.UpdateAvailable);

            return updateInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for updates");
            return updateInfo;
        }
    }

    public async Task<UpdateInstallOutcome> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
        {
            _logger.LogError("No download URL for this platform — refusing to install");
            return UpdateInstallOutcome.Failed;
        }

#if ANDROID
        // Check BEFORE downloading: without this permission the install cannot proceed, and making
        // the operator wait through a ~36 MB download to find that out is the whole complaint.
        if (!CanRequestPackageInstalls())
        {
            _logger.LogWarning("\"Install unknown apps\" is not granted — sending the user to that setting");
            OpenInstallPermissionSettings();
            return UpdateInstallOutcome.PermissionRequired;
        }
#endif

        try
        {
            _logger.LogInformation("Starting download from {Url}", updateInfo.DownloadUrl);

            // Download to a temp file (Android uses the cache dir so the FileProvider can share it).
#if ANDROID
            var tempFile = Path.Combine(FileSystem.CacheDirectory, "PrinterApp_Update.apk");
#else
            var tempFile = Path.Combine(Path.GetTempPath(), "PrinterApp_Update.exe");
#endif

            using (var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var downloadedBytes = 0L;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        downloadedBytes += bytesRead;

                        if (totalBytes > 0)
                        {
                            var progressPercent = (int)((downloadedBytes * 100) / totalBytes);
                            progress?.Report(progressPercent);
                        }
                    }
                }
            }

            _logger.LogInformation("Download complete. File size: {Size} bytes", new FileInfo(tempFile).Length);

            // Verify file was downloaded and has reasonable size
            var fileInfo = new FileInfo(tempFile);
            if (!fileInfo.Exists || fileInfo.Length < 1024)
            {
                _logger.LogError("Downloaded file is invalid or too small");
                return UpdateInstallOutcome.Failed;
            }

            // Install update
            return await InstallUpdateAsync(tempFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading/installing update");
            return UpdateInstallOutcome.Failed;
        }
    }

    private async Task<UpdateInstallOutcome> InstallUpdateAsync(string updateFilePath)
    {
#if ANDROID
        await Task.CompletedTask;
        return InstallApkAndroid(updateFilePath);
#else
        try
        {
            var currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExePath))
            {
                _logger.LogError("Could not determine current exe path");
                return UpdateInstallOutcome.Failed;
            }

            var currentProcessId = Process.GetCurrentProcess().Id;
            var backupPath = currentExePath + ".bak";
            var logPath = Path.Combine(Path.GetTempPath(), "printerapp_update.log");

            _logger.LogInformation("Installing update from {UpdateFile} to {CurrentExe}", updateFilePath, currentExePath);
            _logger.LogInformation("Current process ID: {ProcessId}", currentProcessId);

            // Verify update file exists and is valid
            var updateFileInfo = new FileInfo(updateFilePath);
            if (!updateFileInfo.Exists || updateFileInfo.Length < 1024 * 100) // At least 100KB
            {
                _logger.LogError("Update file is invalid or too small: {Size} bytes", updateFileInfo.Length);
                return UpdateInstallOutcome.Failed;
            }

            // Get current exe size for comparison
            var currentFileInfo = new FileInfo(currentExePath);
            _logger.LogInformation("Current exe size: {CurrentSize} bytes, Update size: {UpdateSize} bytes",
                currentFileInfo.Length, updateFileInfo.Length);

            // Backup current version
            _logger.LogInformation("Creating backup: {Backup}", backupPath);
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            File.Copy(currentExePath, backupPath, true);

            // Verify backup was created
            if (!File.Exists(backupPath))
            {
                _logger.LogError("Failed to create backup file");
                return UpdateInstallOutcome.Failed;
            }

            _logger.LogInformation("Installing update...");

            // Create a robust batch script with better error handling
            var batchScript = Path.Combine(Path.GetTempPath(), "update_printerapp.bat");
            var scriptContent = $@"
@echo off
echo Update process started at %DATE% %TIME% > ""{logPath}""
echo Current EXE: {currentExePath} >> ""{logPath}""
echo Update File: {updateFilePath} >> ""{logPath}""
echo Backup File: {backupPath} >> ""{logPath}""
echo. >> ""{logPath}""

echo Waiting for application to close... >> ""{logPath}""
timeout /t 3 /nobreak > nul

REM Force kill the process if still running
taskkill /F /PID {currentProcessId} > nul 2>&1

echo Waiting additional time for file handles to release... >> ""{logPath}""
timeout /t 2 /nobreak > nul

echo Attempting to replace executable... >> ""{logPath}""
copy /Y ""{updateFilePath}"" ""{currentExePath}""
if errorlevel 1 (
    echo ERROR: Copy failed, attempting retry... >> ""{logPath}""
    timeout /t 2 /nobreak > nul
    copy /Y ""{updateFilePath}"" ""{currentExePath}""
    if errorlevel 1 (
        echo ERROR: Retry failed, restoring from backup... >> ""{logPath}""
        copy /Y ""{backupPath}"" ""{currentExePath}""
        echo Update FAILED - backup restored >> ""{logPath}""
        goto :cleanup
    )
)

echo Verifying file replacement... >> ""{logPath}""
if not exist ""{currentExePath}"" (
    echo ERROR: Target exe missing after copy! >> ""{logPath}""
    copy /Y ""{backupPath}"" ""{currentExePath}""
    echo Update FAILED - backup restored >> ""{logPath}""
    goto :cleanup
)

echo Update successful, cleaning up... >> ""{logPath}""
del /F /Q ""{backupPath}"" > nul 2>&1
del /F /Q ""{updateFilePath}"" > nul 2>&1

:cleanup
echo Restarting application... >> ""{logPath}""
start """" ""{currentExePath}""
echo Update script completed at %DATE% %TIME% >> ""{logPath}""

REM Self-delete and exit
(goto) 2>nul & del ""{batchScript}"" > nul 2>&1
";

            await File.WriteAllTextAsync(batchScript, scriptContent);
            _logger.LogInformation("Batch script created at: {ScriptPath}", batchScript);
            _logger.LogInformation("Update log will be at: {LogPath}", logPath);

            // Start the batch script
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"{batchScript}\"",
                    CreateNoWindow = false, // Show console for debugging
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized
                }
            };

            process.Start();

            _logger.LogInformation("Update script started. App will exit and restart...");

            // Give the batch script time to start
            await Task.Delay(1000);

            // Exit the current app
            Application.Current?.Quit();

            return UpdateInstallOutcome.Started;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing update");
            return UpdateInstallOutcome.Failed;
        }
#endif
    }

#if ANDROID
    /// <summary>
    /// Whether this app may hand an APK to the package installer. Declaring
    /// <c>REQUEST_INSTALL_PACKAGES</c> in the manifest only makes the app eligible to ask — on
    /// Android 8 (API 26) and up the user must additionally grant "Install unknown apps" for this
    /// specific app, and a device that has only ever been sideloaded once has not.
    /// <para>Below API 26 the setting is device-wide with no per-app query, so there is nothing to
    /// check and nowhere to send the user; the installer's own prompt handles it. minSdk here is 24,
    /// so this branch is reachable — calling the API 26 method unguarded would crash Android 7.</para>
    /// </summary>
    private static bool CanRequestPackageInstalls()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return true;

        return Android.App.Application.Context.PackageManager?.CanRequestPackageInstalls() ?? false;
    }

    /// <summary>
    /// Opens the per-app "Install unknown apps" screen for this app. Best-effort: the operator
    /// pressed Update Now, so taking them straight to the one toggle that blocks it beats a dead end.
    /// </summary>
    private void OpenInstallPermissionSettings()
    {
        // Statically guarded, not merely unreachable: today CanRequestPackageInstalls() returns true
        // below API 26 so this is never called there, but that is a coupling across two methods, and
        // the settings action itself does not exist before 26 (minSdk is 24).
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        try
        {
            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            // Some OEM builds hide this screen. The updater still reports PermissionRequired, so the
            // operator is told what to enable even when we cannot navigate them to it.
            _logger.LogError(ex, "Could not open the \"install unknown apps\" settings screen");
        }
    }

    // Android can't silently self-update; hand the downloaded APK to the system package installer,
    // which prompts the user to confirm. The APK is signed with the same key as the running app
    // (verified: same signer cert across releases), so it installs in place as an update — the
    // operator's config.json, API key and printer settings survive.
    private UpdateInstallOutcome InstallApkAndroid(string apkPath)
    {
        try
        {
            var context = Android.App.Application.Context;
            var apkFile = new Java.IO.File(apkPath);
            var authority = context.PackageName + ".fileprovider";
            var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, apkFile);

            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
            intent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
            intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.GrantReadUriPermission);
            context.StartActivity(intent);

            _logger.LogInformation("Launched the Android package installer for {Apk}", apkPath);
            return UpdateInstallOutcome.Started;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch the Android APK installer");
            return UpdateInstallOutcome.Failed;
        }
    }
#endif
}
