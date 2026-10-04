using PrinterAPP.ViewModels;

namespace PrinterAPP;

public partial class PrinterCorrectionsPage : ContentPage
{
    private readonly PrinterCorrectionHistoryViewModel _viewModel;
    private bool _copyConfirmationOpen;

    public PrinterCorrectionsPage(PrinterCorrectionHistoryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.RefreshHistory();
    }

    private void OnRefreshClicked(object? sender, EventArgs e) => _viewModel.RefreshHistory();

    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        if (_copyConfirmationOpen || sender is not Button { CommandParameter: PrinterCorrectionHistoryItem item })
            return;

        // Set the guard before the first await so a second tap cannot queue another confirmation.
        _copyConfirmationOpen = true;
        try
        {
            if (!item.CanCopy || !_viewModel.CanCopy(item.Key))
                return;

            var confirmed = await DisplayAlertAsync(
                "Send a correction COPY?",
                $"{PrinterCorrectionHistoryViewModel.CopyConfirmationWarning} This COPY does not change the original status.",
                "Print COPY",
                "Cancel");
            if (!confirmed)
                return;

            var result = await _viewModel.PrintCopyAsync(item.Key);
            await DisplayAlertAsync(
                "Correction COPY",
                PrinterCorrectionHistoryViewModel.CopyResultMessage(result),
                "OK");
        }
        finally
        {
            _copyConfirmationOpen = false;
            _viewModel.RefreshHistory();
        }
    }
}
