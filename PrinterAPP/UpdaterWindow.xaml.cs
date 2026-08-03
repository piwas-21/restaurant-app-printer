using PrinterAPP.Converters;
using PrinterAPP.Models;
using PrinterAPP.Services;

namespace PrinterAPP;

public partial class UpdaterWindow : ContentPage
{
    private readonly IUpdateService _updateService;
    private UpdateInfo? _updateInfo;
    private bool _autoChecked;

    public UpdaterWindow(IUpdateService updateService)
    {
        InitializeComponent();
        _updateService = updateService;

        // Show current version immediately
        CurrentVersionLabel.Text = _updateService.GetCurrentVersion();
    }

    // Check as soon as the updater opens so the user doesn't have to tap "Check" first — the
    // manual Update button and the startup update prompt both land here.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_autoChecked) return;
        _autoChecked = true;
        OnCheckForUpdatesClicked(this, EventArgs.Empty);
    }

    private async void OnCheckForUpdatesClicked(object sender, EventArgs e)
    {
        try
        {
            CheckButton.IsEnabled = false;
            StatusLabel.Text = "Checking for updates...";
            StatusLabel.TextColor = CraftColors.Muted;

            _updateInfo = await _updateService.CheckForUpdateAsync();

            if (_updateInfo.UpdateAvailable)
            {
                // Update available
                LatestVersionLabel.Text = _updateInfo.LatestVersion;
                LatestVersionFrame.IsVisible = true;

                if (!string.IsNullOrWhiteSpace(_updateInfo.ReleaseNotes))
                {
                    ReleaseNotesLabel.Text = _updateInfo.ReleaseNotes;
                    ReleaseNotesFrame.IsVisible = true;
                }

                UpdateButton.IsVisible = true;
                StatusLabel.Text = $"New version {_updateInfo.LatestVersion} is available!";
                StatusLabel.TextColor = CraftColors.SuccessText;
            }
            else
            {
                // No update available
                LatestVersionLabel.Text = _updateInfo.LatestVersion;
                LatestVersionFrame.IsVisible = true;
                StatusLabel.Text = "You have the latest version!";
                StatusLabel.TextColor = CraftColors.SuccessText;
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error checking for updates: {ex.Message}";
            StatusLabel.TextColor = CraftColors.Error;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnUpdateNowClicked(object sender, EventArgs e)
    {
        if (_updateInfo == null || !_updateInfo.UpdateAvailable)
            return;

        try
        {
            UpdateButton.IsEnabled = false;
            CheckButton.IsEnabled = false;
            CloseButton.IsEnabled = false;

            DownloadProgressBar.IsVisible = true;
            StatusLabel.Text = "Downloading update...";
            StatusLabel.TextColor = CraftColors.Muted;

            var progress = new Progress<int>(percent =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    DownloadProgressBar.Progress = percent / 100.0;
                    StatusLabel.Text = $"Downloading update... {percent}%";
                });
            });

            var outcome = await _updateService.DownloadAndInstallUpdateAsync(_updateInfo, progress);

            switch (outcome)
            {
                case UpdateInstallOutcome.Started:
                    StatusLabel.Text = "Update successful! App will restart...";
                    StatusLabel.TextColor = CraftColors.SuccessText;
                    break;

                // Not a failure and not a success: the download never started because Android blocks
                // the install until this app is allowed to install unknown apps. Say what to do —
                // reporting "successful" here (the old behaviour) left the operator on a stale build
                // believing they had updated.
                case UpdateInstallOutcome.PermissionRequired:
                    StatusLabel.Text = "Allow \"Install unknown apps\" for PrinterApp in the settings "
                                     + "screen that just opened, then press Update Now again.";
                    StatusLabel.TextColor = CraftColors.WarningText;
                    DownloadProgressBar.IsVisible = false;
                    ReEnableControls();
                    break;

                default:
                    StatusLabel.Text = "Update failed. Please try again or download manually.";
                    StatusLabel.TextColor = CraftColors.Error;
                    ReEnableControls();
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error installing update: {ex.Message}";
            StatusLabel.TextColor = CraftColors.Error;
            ReEnableControls();
        }
    }

    /// <summary>Hands the screen back to the operator after an update attempt that did not launch.</summary>
    private void ReEnableControls()
    {
        UpdateButton.IsEnabled = true;
        CheckButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }
}
