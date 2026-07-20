// MainPage.xaml.cs
using Microsoft.Maui.Controls;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Sentry;

namespace PrinterAPP;

public partial class MainPage : ContentPage
{
    private readonly IPrinterService _printerService;
    private readonly IEventStreamingService _eventStreamingService;
    private readonly IOrderPrintService _orderPrintService;
    private readonly IOrderHistoryService _orderHistoryService;
    private readonly IUpdateService _updateService;
    private readonly IPrinterTestService _printerTestService;
    private readonly IDeviceIdentityService _deviceIdentity;
    private readonly ITelemetryScheduler _telemetryScheduler;
    private readonly ILogger<MainPage> _logger;
    private PrinterConfiguration _config;
    private bool _isServiceRunning = false;

    public MainPage(
        IPrinterService printerService,
        IEventStreamingService eventStreamingService,
        IOrderPrintService orderPrintService,
        IOrderHistoryService orderHistoryService,
        IUpdateService updateService,
        IPrinterTestService printerTestService,
        IDeviceIdentityService deviceIdentity,
        ITelemetryScheduler telemetryScheduler,
        ILogger<MainPage> logger)
    {
        InitializeComponent();
        _printerService = printerService;
        _eventStreamingService = eventStreamingService;
        _orderPrintService = orderPrintService;
        _orderHistoryService = orderHistoryService;
        _updateService = updateService;
        _printerTestService = printerTestService;
        _deviceIdentity = deviceIdentity;
        _telemetryScheduler = telemetryScheduler;
        _logger = logger;
        _config = new PrinterConfiguration();

        // Subscribe to events
        _eventStreamingService.OrderReceived += OnOrderReceived;
        _eventStreamingService.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Initialize the UI asynchronously
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Show loading state
            StatusLabel.Text = "Loading configuration...";
            StatusLabel.TextColor = Colors.Gray;

            // Load saved configuration
            _config = await _printerService.LoadConfigurationAsync();

            // Tag Sentry events with this device + tenant so fleet errors are attributable (no-op when
            // Sentry is inert). Here — after startup + config load — so the lazy device-id persists.
            _deviceIdentity.ApplySentryTags(_config.TenantSlug);

            // Begin periodic heartbeats. Runs regardless of feed state so a stopped/wedged feed is
            // remotely visible (the 2026-07-19 incident's blind spot). Idempotent + self-guarding.
            _telemetryScheduler.Start();

            // Debug logging
            _logger.LogInformation("Loaded API URL from config: {ApiUrl}", _config.ApiBaseUrl);
            System.Diagnostics.Debug.WriteLine($"DEBUG: Loaded API URL = {_config.ApiBaseUrl}");

            // Update UI with loaded configuration
            ApiUrlEntry.Text = _config.ApiBaseUrl;
            ApiKeyEntry.Text = _config.ApiKey;

            // Debug logging
            _logger.LogInformation("Set ApiUrlEntry.Text to: {ApiUrl}", ApiUrlEntry.Text);
            System.Diagnostics.Debug.WriteLine($"DEBUG: ApiUrlEntry.Text = {ApiUrlEntry.Text}");
            RestaurantNameEntry.Text = _config.RestaurantName;
            KitchenLocationEntry.Text = _config.KitchenLocation;

            // Kitchen printer settings
            KitchenAutoPrintSwitch.IsToggled = _config.KitchenAutoPrint;
            KitchenPrintCopiesEntry.Text = _config.KitchenPrintCopies.ToString();
            KitchenPaperWidthPicker.SelectedIndex = _config.KitchenPaperWidth == 80 ? 0 : 1;

            // Cashier printer settings
            CashierAutoPrintSwitch.IsToggled = _config.CashierAutoPrint;
            CashierPrintCopiesEntry.Text = _config.CashierPrintCopies.ToString();
            CashierPaperWidthPicker.SelectedIndex = _config.CashierPaperWidth == 80 ? 0 : 1;

            // Time restriction settings
            EnableTimeRestrictionSwitch.IsToggled = _config.EnableTimeRestriction;
            RestrictStartTimePicker.Time = _config.RestrictStartTime;
            RestrictEndTimePicker.Time = _config.RestrictEndTime;

            // Load available printers
            await LoadPrintersAsync();

            // Round-trip a saved network IP into the IP entry (the picker can't hold an IP, and on
            // Android it enumerates nothing).
            if (PrinterEndpoint.TryParse(_config.KitchenPrinterName, out _, out _))
                KitchenPrinterIpEntry.Text = _config.KitchenPrinterName;
            if (PrinterEndpoint.TryParse(_config.CashierPrinterName, out _, out _))
                CashierPrinterIpEntry.Text = _config.CashierPrinterName;

            // Reflect the current feed state (cross-platform).
            UpdateServiceStatus();

            // Auto-start the order feed on launch. Windows keeps honouring the persisted
            // IsServiceRunning flag (unchanged). On Android the manual Start control was historically
            // absent and existing installs could never persist IsServiceRunning=true, so a configured
            // ApiBaseUrl is treated as intent to listen. ApiBaseUrl defaults to a non-empty value, so
            // in practice the Android feed always comes up on launch — the intended behaviour for an
            // always-on printer appliance (a Stop tap lasts the session, not across restarts). Without
            // this the feed never starts on Android and no orders are ever fetched; the feed is plain
            // HTTP, not Windows-specific, so it is safe to run everywhere.
            bool autoStartFeed =
#if WINDOWS
                _config.IsServiceRunning;
#else
                _config.IsServiceRunning || !string.IsNullOrWhiteSpace(_config.ApiBaseUrl);
#endif
            if (autoStartFeed && !_eventStreamingService.IsListening)
            {
                try
                {
                    _logger.LogInformation("Auto-starting order feed based on saved configuration");
                    await _eventStreamingService.StartListeningAsync();
                    _isServiceRunning = true;
                    UpdateServiceStatus();
                    StatusLabel.Text = $"Service auto-started - API: {_config.ApiBaseUrl}";
                    StatusLabel.TextColor = Colors.Green;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to auto-start order feed");
                    // Surface a failed feed start to the fleet dashboard — this is the class of
                    // failure behind the 2026-07-19 incident. No-op when Sentry is inert.
                    SentrySdk.CaptureException(ex);
                    StatusLabel.Text = "Failed to auto-start service";
                    StatusLabel.TextColor = Colors.Orange;
                }
            }
            else
            {
                StatusLabel.Text = $"Configuration loaded - API: {_config.ApiBaseUrl}";
                StatusLabel.TextColor = Colors.Green;
            }

            // Auto-check for an app update in the background so the customer no longer needs a
            // release link or a manual reinstall — if a newer version is out, we offer it.
            _ = CheckForUpdatesOnStartupAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error loading configuration";
            StatusLabel.TextColor = Colors.Red;
            await DisplayAlert("Error", $"Failed to initialize: {ex.Message}", "OK");
        }
    }

    private async Task LoadPrintersAsync()
    {
        try
        {
            var printers = await _printerService.GetAvailablePrintersAsync();

            if (printers != null && printers.Count > 0)
            {
                // Load both kitchen and cashier printer pickers
                KitchenPrinterPicker.ItemsSource = printers;
                CashierPrinterPicker.ItemsSource = printers;

                // Select saved kitchen printer if it exists
                if (!string.IsNullOrEmpty(_config.KitchenPrinterName))
                {
                    var savedPrinter = printers.FirstOrDefault(p => p.Contains(_config.KitchenPrinterName));
                    if (savedPrinter != null)
                    {
                        KitchenPrinterPicker.SelectedItem = savedPrinter;
                    }
                    else if (printers.Count > 0)
                    {
                        KitchenPrinterPicker.SelectedIndex = 0;
                    }
                }
                else if (printers.Count > 0)
                {
                    KitchenPrinterPicker.SelectedIndex = 0;
                }

                // Select saved cashier printer if it exists
                if (!string.IsNullOrEmpty(_config.CashierPrinterName))
                {
                    var savedPrinter = printers.FirstOrDefault(p => p.Contains(_config.CashierPrinterName));
                    if (savedPrinter != null)
                    {
                        CashierPrinterPicker.SelectedItem = savedPrinter;
                    }
                    else if (printers.Count > 1)
                    {
                        CashierPrinterPicker.SelectedIndex = 1; // Default to second printer if available
                    }
                    else if (printers.Count > 0)
                    {
                        CashierPrinterPicker.SelectedIndex = 0;
                    }
                }
                else if (printers.Count > 1)
                {
                    CashierPrinterPicker.SelectedIndex = 1; // Default to second printer
                }
                else if (printers.Count > 0)
                {
                    CashierPrinterPicker.SelectedIndex = 0;
                }
            }
            else
            {
                await DisplayAlert("Info", "No printers found. Make sure printers are installed.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to load printers: {ex.Message}", "OK");
        }
    }

    private void UpdateServiceStatus()
    {
        _isServiceRunning = _eventStreamingService.IsListening;

        if (_isServiceRunning)
        {
            StatusLabel.Text = "SSE Service is running";
            StatusLabel.TextColor = Colors.Green;
            ServiceToggleButton.Text = "Stop Service";
            ServiceToggleButton.BackgroundColor = Colors.Red;
        }
        else
        {
            StatusLabel.Text = "SSE Service is stopped";
            StatusLabel.TextColor = Colors.Orange;
            ServiceToggleButton.Text = "Start Service";
            ServiceToggleButton.BackgroundColor = Colors.Green;
        }
    }

    // Event Handlers

    private void OnApiUrlChanged(object sender, TextChangedEventArgs e)
    {
        // Check if URL has changed from saved config
        var currentUrl = ApiUrlEntry.Text?.Trim();
        var savedUrl = _config.ApiBaseUrl?.Trim();
        bool hasChanged = !string.Equals(currentUrl, savedUrl, StringComparison.OrdinalIgnoreCase);

        if (hasChanged && !string.IsNullOrWhiteSpace(currentUrl))
        {
            ApiUrlChangeLabel.Text = "⚠️ API URL changed - Click 'Save Configuration' to apply";
            ApiUrlChangeLabel.IsVisible = true;
        }
        else
        {
            ApiUrlChangeLabel.IsVisible = false;
        }
    }

    private async void OnServiceToggleClicked(object sender, EventArgs e)
    {
        try
        {
            ServiceToggleButton.IsEnabled = false;

            if (_isServiceRunning)
            {
                // Stop SSE service
                StatusLabel.Text = "Stopping SSE service...";
                StatusLabel.TextColor = Colors.Orange;

                await _eventStreamingService.StopListeningAsync();
                _isServiceRunning = false;
                _config.IsServiceRunning = false;
                await _printerService.SaveConfigurationAsync(_config);

                await DisplayAlert("Success", "SSE service stopped successfully", "OK");
            }
            else
            {
                // Start SSE service
                StatusLabel.Text = "Starting SSE service...";
                StatusLabel.TextColor = Colors.Orange;

                await _eventStreamingService.StartListeningAsync();
                _isServiceRunning = true;
                _config.IsServiceRunning = true;
                await _printerService.SaveConfigurationAsync(_config);

                await DisplayAlert("Success", "SSE service started successfully. Now listening for orders.", "OK");
            }

            UpdateServiceStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle SSE service");
            await DisplayAlert("Error", $"Failed to toggle service: {ex.Message}", "OK");
        }
        finally
        {
            ServiceToggleButton.IsEnabled = true;
        }
    }

    private void OnOrderReceived(object? sender, OrderEvent orderEvent)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _logger.LogInformation("Order received: #{OrderId} - {EventType}",
                    orderEvent.Order?.Id, orderEvent.EventType);

                if (orderEvent.Order == null)
                {
                    return;
                }

                // Add order to history
                _orderHistoryService.AddOrder(orderEvent);

                // Print to all appropriate printers (Cashier + FrontKitchen + BackKitchen)
                var (cashierSuccess, frontKitchenSuccess, backKitchenSuccess) =
                    await _orderPrintService.PrintOrderToAllPrintersAsync(orderEvent.Order);

                // Update print status in history (combine kitchen results)
                var kitchenSuccess = frontKitchenSuccess && backKitchenSuccess;
                _orderHistoryService.UpdatePrintStatus(orderEvent.Order.Id, kitchenSuccess, cashierSuccess);

                StatusLabel.Text = $"Order #{orderEvent.Order.OrderNumber} printed";
                StatusLabel.TextColor = Colors.Green;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing order");
                StatusLabel.Text = "Error processing order";
                StatusLabel.TextColor = Colors.Red;
            }
        });
    }

    private void OnConnectionStatusChanged(object? sender, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            StatusLabel.TextColor = status.Contains("Error") || status.Contains("Disconnected")
                ? Colors.Red
                : Colors.Green;
        });
    }

    private async void OnTestApiClicked(object sender, EventArgs e)
    {
        try
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;

            StatusLabel.Text = "Testing API connection...";
            StatusLabel.TextColor = Colors.Orange;

            var apiUrl = ApiUrlEntry.Text?.Trim();
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                await DisplayAlert("Error", "Please enter an API URL", "OK");
                return;
            }

            // Test the actual order feed the app polls, using the entered key. This catches the real
            // production failure mode — a 401 from a missing/incorrect key — which the previous
            // events-endpoint check masked by treating 401 as success.
            var status = await _printerService.TestPrinterFeedAsync(apiUrl, ApiKeyEntry.Text?.Trim());

            if (status is null)
            {
                StatusLabel.Text = "API connection failed";
                StatusLabel.TextColor = Colors.Red;
                await DisplayAlert("Connection failed",
                    "Could not reach the server. Check the API URL and the tablet's internet connection.", "OK");
            }
            else if ((int)status >= 200 && (int)status < 300)
            {
                StatusLabel.Text = "API connection successful";
                StatusLabel.TextColor = Colors.Green;
                await DisplayAlert("Success", "Connected and the API key was accepted. The app can receive orders.", "OK");
            }
            else if (status == System.Net.HttpStatusCode.Unauthorized)
            {
                StatusLabel.Text = "API key missing or incorrect";
                StatusLabel.TextColor = Colors.Red;
                await DisplayAlert("Unauthorized (401)",
                    "Connected to the server, but the API key is missing or incorrect. Paste the printer API key for this restaurant, then tap Save.", "OK");
            }
            else
            {
                StatusLabel.Text = $"API returned {(int)status}";
                StatusLabel.TextColor = Colors.Red;
                await DisplayAlert("Unexpected response",
                    $"The server responded with {(int)status} ({status}). Check the API URL.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Connection test failed: {ex.Message}", "OK");
        }
        finally
        {
            if (sender is Button button) button.IsEnabled = true;
        }
    }

    private async void OnRefreshPrintersClicked(object sender, EventArgs e)
    {
        try
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;

            StatusLabel.Text = "Refreshing printer list...";
            StatusLabel.TextColor = Colors.Orange;

            await LoadPrintersAsync();

            StatusLabel.Text = "Printer list refreshed";
            StatusLabel.TextColor = Colors.Green;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to refresh printers: {ex.Message}", "OK");
        }
        finally
        {
            if (sender is Button button) button.IsEnabled = true;
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        try
        {
            // Validate any manually-entered printer IPs up front so we don't save an unusable value
            // (which would later be mis-routed to the Windows spooler as a "printer name").
            foreach (var (entry, label) in new[]
                     {
                         (KitchenPrinterIpEntry.Text, "Kitchen"),
                         (CashierPrinterIpEntry.Text, "Cashier"),
                     })
            {
                if (!string.IsNullOrWhiteSpace(entry) && !PrinterEndpoint.TryParse(entry, out _, out _))
                {
                    await DisplayAlert("Invalid printer IP",
                        $"'{entry.Trim()}' is not a valid {label} printer IP. Use e.g. 192.168.1.50 or 192.168.1.50:9100.",
                        "OK");
                    return;
                }
            }

            // Check if API URL has changed
            var newApiUrl = ApiUrlEntry.Text?.Trim();
            var oldApiUrl = _config.ApiBaseUrl?.Trim();
            bool apiUrlChanged = !string.Equals(newApiUrl, oldApiUrl, StringComparison.OrdinalIgnoreCase);

            // If API URL changed and service is running, stop the service first
            if (apiUrlChanged && _isServiceRunning)
            {
                var confirm = await DisplayAlert(
                    "Service Running",
                    "The API URL has changed. The service must be stopped before saving the new URL. Stop the service now?",
                    "Yes, Stop Service",
                    "Cancel");

                if (!confirm)
                {
                    StatusLabel.Text = "Configuration save cancelled";
                    StatusLabel.TextColor = Colors.Orange;
                    return;
                }

                // Stop the service
                StatusLabel.Text = "Stopping service due to API URL change...";
                StatusLabel.TextColor = Colors.Orange;

                try
                {
                    await _eventStreamingService.StopListeningAsync();
                    _isServiceRunning = false;
                    _config.IsServiceRunning = false;

                    UpdateServiceStatus();

                    await DisplayAlert("Service Stopped", "The service has been stopped. Please start it again after saving to use the new API URL.", "OK");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stop service before URL change");
                    await DisplayAlert("Error", $"Failed to stop service: {ex.Message}\nPlease stop the service manually before changing the API URL.", "OK");
                    return;
                }
            }

            // Update configuration from UI
            _config.ApiBaseUrl = newApiUrl;
            _config.ApiKey = ApiKeyEntry.Text?.Trim() ?? string.Empty;
            _config.RestaurantName = RestaurantNameEntry.Text;
            _config.KitchenLocation = KitchenLocationEntry.Text;

            // Kitchen printer settings
            _config.KitchenAutoPrint = KitchenAutoPrintSwitch.IsToggled;
            _config.KitchenPaperWidth = KitchenPaperWidthPicker.SelectedIndex == 0 ? 80 : 58;
            if (int.TryParse(KitchenPrintCopiesEntry.Text, out int kitchenCopies))
            {
                _config.KitchenPrintCopies = Math.Max(1, Math.Min(kitchenCopies, 5)); // Limit 1-5
            }
            // A manually-entered network IP takes precedence over the spooler picker (and is the
            // only way to set a printer on Android, where the picker enumerates nothing).
            if (!string.IsNullOrWhiteSpace(KitchenPrinterIpEntry.Text))
            {
                _config.KitchenPrinterName = KitchenPrinterIpEntry.Text.Trim();
            }
            else if (KitchenPrinterPicker.SelectedItem != null)
            {
                _config.KitchenPrinterName = KitchenPrinterPicker.SelectedItem.ToString()!.Replace(" (Default)", "").Trim();
            }

            // Cashier printer settings
            _config.CashierAutoPrint = CashierAutoPrintSwitch.IsToggled;
            _config.CashierPaperWidth = CashierPaperWidthPicker.SelectedIndex == 0 ? 80 : 58;
            if (int.TryParse(CashierPrintCopiesEntry.Text, out int cashierCopies))
            {
                _config.CashierPrintCopies = Math.Max(1, Math.Min(cashierCopies, 5)); // Limit 1-5
            }
            if (!string.IsNullOrWhiteSpace(CashierPrinterIpEntry.Text))
            {
                _config.CashierPrinterName = CashierPrinterIpEntry.Text.Trim();
            }
            else if (CashierPrinterPicker.SelectedItem != null)
            {
                _config.CashierPrinterName = CashierPrinterPicker.SelectedItem.ToString()!.Replace(" (Default)", "").Trim();
            }

            // Time restriction settings
            _config.EnableTimeRestriction = EnableTimeRestrictionSwitch.IsToggled;
            // MAUI 10 made TimePicker.Time nullable (TimeSpan?); the config fields are non-nullable.
            // Preserve the existing saved value if the picker somehow reports null (rather than
            // silently resetting the restriction window to midnight).
            _config.RestrictStartTime = RestrictStartTimePicker.Time ?? _config.RestrictStartTime;
            _config.RestrictEndTime = RestrictEndTimePicker.Time ?? _config.RestrictEndTime;

            // Save configuration
            _logger.LogInformation("Saving configuration with API URL: {ApiUrl}", _config.ApiBaseUrl);
            System.Diagnostics.Debug.WriteLine($"DEBUG SAVE: Saving config with API URL = {_config.ApiBaseUrl}");

            await _printerService.SaveConfigurationAsync(_config);

            _logger.LogInformation("Configuration saved successfully");
            System.Diagnostics.Debug.WriteLine($"DEBUG SAVE: Configuration saved successfully");

            // Hide the URL change warning
            ApiUrlChangeLabel.IsVisible = false;

            StatusLabel.Text = "Configuration saved";
            StatusLabel.TextColor = Colors.Green;

            var configPath = _printerService.ConfigFilePath;

            if (apiUrlChanged)
            {
                await DisplayAlert("Success",
                    $"Configuration saved successfully!\n\n" +
                    $"API URL: {_config.ApiBaseUrl}\n" +
                    $"Config file: {configPath}\n\n" +
                    $"Please start the service again to connect to the new API endpoint.",
                    "OK");
            }
            else
            {
                await DisplayAlert("Success",
                    $"Configuration saved successfully!\n\n" +
                    $"Config file: {configPath}",
                    "OK");
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Failed to save configuration";
            StatusLabel.TextColor = Colors.Red;
            await DisplayAlert("Error", $"Failed to save configuration: {ex.Message}", "OK");
        }
    }

    private async void OnTestPrintClicked(object sender, EventArgs e)
    {
        try
        {
            // Resolve each printer's UI inputs into a transport target (network IP entry wins
            // over the spooler picker; null = not configured). Routing lives in the services.
            var kitchenTarget = PrinterTestTarget.Resolve(
                KitchenPrinterIpEntry.Text, KitchenPrinterPicker.SelectedItem?.ToString());
            var cashierTarget = PrinterTestTarget.Resolve(
                CashierPrinterIpEntry.Text, CashierPrinterPicker.SelectedItem?.ToString());
            if (kitchenTarget is null && cashierTarget is null)
            {
                await DisplayAlert("Error", "Please select a printer or enter a network printer IP first", "OK");
                return;
            }

            var button = sender as Button;
            if (button != null) button.IsEnabled = false;

            StatusLabel.Text = "Printing test receipts...";
            StatusLabel.TextColor = Colors.Orange;

            // Update config with current UI values
            _config.RestaurantName = RestaurantNameEntry.Text;
            _config.KitchenLocation = KitchenLocationEntry.Text;
            _config.KitchenPaperWidth = KitchenPaperWidthPicker.SelectedIndex == 0 ? 80 : 58;
            _config.CashierPaperWidth = CashierPaperWidthPicker.SelectedIndex == 0 ? 80 : 58;

            var results = new List<string>();

            // PrinterTestService routes per target kind (network TCP vs Windows spooler) through
            // the IPrinterTransport seam — no transport special-casing here (ADR-006 Phase 2b).
            if (kitchenTarget is not null)
                results.Add(await _printerTestService.TestPrinterAsync(kitchenTarget, _config, "KITCHEN", "Kitchen"));

            if (cashierTarget is not null)
                results.Add(await _printerTestService.TestPrinterAsync(cashierTarget, _config, "CASHIER", "Cashier"));

            StatusLabel.Text = "Test receipts printed";
            StatusLabel.TextColor = Colors.Green;
            await DisplayAlert("Test Results", string.Join("\n", results), "OK");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Print error";
            StatusLabel.TextColor = Colors.Red;
            await DisplayAlert("Error", $"Print failed: {ex.Message}", "OK");
        }
        finally
        {
            if (sender is Button button) button.IsEnabled = true;
        }
    }

    private async void OnResetClicked(object sender, EventArgs e)
    {
        try
        {
            var confirm = await DisplayAlert(
                "Confirm Reset",
                "Are you sure you want to reset all settings to default?",
                "Yes",
                "No");

            if (confirm)
            {
                // Reset to defaults
                _config = new PrinterConfiguration();

                // Update UI
                ApiUrlEntry.Text = _config.ApiBaseUrl;
                ApiKeyEntry.Text = _config.ApiKey;
                RestaurantNameEntry.Text = _config.RestaurantName;
                KitchenLocationEntry.Text = _config.KitchenLocation;

                // Kitchen printer settings
                KitchenAutoPrintSwitch.IsToggled = _config.KitchenAutoPrint;
                KitchenPrintCopiesEntry.Text = _config.KitchenPrintCopies.ToString();
                KitchenPaperWidthPicker.SelectedIndex = _config.KitchenPaperWidth == 80 ? 0 : 1;

                // Cashier printer settings
                CashierAutoPrintSwitch.IsToggled = _config.CashierAutoPrint;
                CashierPrintCopiesEntry.Text = _config.CashierPrintCopies.ToString();
                CashierPaperWidthPicker.SelectedIndex = _config.CashierPaperWidth == 80 ? 0 : 1;

                // Time restriction settings
                EnableTimeRestrictionSwitch.IsToggled = _config.EnableTimeRestriction;
                RestrictStartTimePicker.Time = _config.RestrictStartTime;
                RestrictEndTimePicker.Time = _config.RestrictEndTime;

                if (KitchenPrinterPicker.ItemsSource != null && KitchenPrinterPicker.ItemsSource.Cast<object>().Any())
                {
                    KitchenPrinterPicker.SelectedIndex = 0;
                }

                if (CashierPrinterPicker.ItemsSource != null && CashierPrinterPicker.ItemsSource.Cast<object>().Any())
                {
                    CashierPrinterPicker.SelectedIndex = CashierPrinterPicker.ItemsSource.Cast<object>().Count() > 1 ? 1 : 0;
                }

                StatusLabel.Text = "Settings reset to default";
                StatusLabel.TextColor = Colors.Orange;

                await DisplayAlert("Success", "Settings have been reset to default values", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to reset settings: {ex.Message}", "OK");
        }
    }

    private async void OnPrintStylesClicked(object sender, EventArgs e)
    {
        try
        {
            var settingsPage = new PrinterAPP.Pages.PrintStyleSettingsPage();
            await Navigation.PushAsync(settingsPage);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to open print styles: {ex.Message}", "OK");
        }
    }

    private async void OnCheckForUpdatesClicked(object sender, EventArgs e)
    {
        try
        {
            var updaterWindow = new UpdaterWindow(_updateService);
            await Navigation.PushModalAsync(updaterWindow);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to open updater: {ex.Message}", "OK");
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(4000); // let the UI settle and the feed start first
            var info = await _updateService.CheckForUpdateAsync();
            if (!info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl))
                return;

            var update = await DisplayAlert(
                "Update available",
                $"Version {info.LatestVersion} is available (you have {info.CurrentVersion}). Update now?",
                "Update", "Later");
            if (update)
            {
                await Navigation.PushModalAsync(new UpdaterWindow(_updateService));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup update check failed");
        }
    }
}
