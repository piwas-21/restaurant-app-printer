using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public class OrderPrintService : IOrderPrintService
{
    private readonly IMarketplaceReceiptComposer _marketplace;
    private readonly IPrinterService _printerService;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<OrderPrintService> _logger;
    private readonly PrintStyleSettingsService _styleService;
    private readonly IDeviceIdentityService? _deviceIdentity;
    private readonly IPrinterOutputService _printerOutputService;

    // KitchenType values as the backend emits them (OrderItemDto.KitchenType).
    private const string FRONT_KITCHEN = "FrontKitchen";
    private const string BACK_KITCHEN = "BackKitchen";
    private const decimal MinorUnitsPerCurrencyUnit = 100m;

    public OrderPrintService(
        IMarketplaceReceiptComposer marketplace,
        IPrinterService printerService,
        IRequestLogService requestLogService,
        ILogger<OrderPrintService> logger,
        IAppDataPathProvider pathProvider,
        IDeviceIdentityService? deviceIdentity = null,
        IPrinterOutputService? printerOutputService = null)
    {
        _marketplace = marketplace;
        _printerService = printerService;
        _requestLogService = requestLogService;
        _deviceIdentity = deviceIdentity;
        _logger = logger;
        _printerOutputService = printerOutputService
            ?? new PrinterOutputService(NullLogger<PrinterOutputService>.Instance);
        // Path provider injected (was `new PrintStyleSettingsService()` with a MAUI default) so this
        // service is source-linkable into the headless print-to-sink test.
        _styleService = new PrintStyleSettingsService(pathProvider);
    }

    /// <summary>
    /// Styles are loaded PER PRINT, never cached in a field. This service is a singleton, and a
    /// settings object captured at construction meant every ticket after a settings save still
    /// printed the styles the app started with — the partner report of "changed the text format
    /// settings, saved, no change in prints at all". LoadSettings caches internally, so a print
    /// reads at most one file stat, and a save is visible to the very next ticket.
    /// </summary>
    private PrintStyleSettings CurrentStyles() => _styleService.LoadSettings();

    /// <summary>
    /// Applies a section style and returns the ESC/POS commands
    /// </summary>
    private string ApplyStyle(SectionStyle style)
    {
        var (sizeCmd, boldCmd, alignCmd) = _styleService.GetEscPosCommands(style);
        return alignCmd + sizeCmd + boldCmd;
    }

    /// <summary>
    /// Resets formatting after applying a style
    /// </summary>
    private string ResetStyle(SectionStyle style)
    {
        return _styleService.GetResetCommands(style);
    }

    /// <summary>
    /// Prints order to every destination it owes work: always Cashier (full receipt with prices),
    /// then the kitchen tickets the routing policy asks for. Stations keeps the legacy Front/Back
    /// split byte-for-byte and routes unassigned work to a Default ticket; SingleKitchen composes
    /// ONE General ticket instead and owes the stations nothing.
    /// </summary>
    public async Task<(bool Cashier, KitchenPrintOutcome FrontKitchen, KitchenPrintOutcome BackKitchen, KitchenPrintOutcome GeneralDefault)>
        PrintOrderToAllPrintersAsync(
            Order order,
            bool isManualPrint = false,
            CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🖨️ Printing order {OrderNumber} for {Table} to all printers",
            order.OrderNumber, TableDisplay.ResolveLabel(order.TableLabel, order.TableNumber) ?? "no table");

        var config = await _printerService.LoadConfigurationAsync();
        var policy = new KitchenRoutingPolicy(config.KitchenRoutingMode);
        // Manual reprints are an operator-directed local action and preserve the existing "all
        // configured destinations" behavior. Only the unattended feed path is device-routed.
        var routeSelection = isManualPrint
            ? OrderRoutingStateValidation.RoutingSelection.Legacy
            : OrderRoutingStateValidation.Select(order, _deviceIdentity?.DeviceId);
        if (!routeSelection.IsLegacy && !routeSelection.IsValid)
        {
            _logger.LogError("Rejected routed order {OrderNumber}: {Reason}",
                order.OrderNumber, routeSelection.FailureReason);
            _requestLogService.LogError(
                "Printer Routing", $"Rejected routed order {order.OrderNumber}",
                routeSelection.FailureReason ?? "Invalid route state");
            return (false, KitchenPrintOutcome.Unknown, KitchenPrintOutcome.Unknown,
                KitchenPrintOutcome.Unknown);
        }

        if (!routeSelection.IsLegacy && routeSelection.EligibleTargets.Count == 0)
        {
            _logger.LogWarning("No queued route assigned to this device for order {OrderNumber}",
                order.OrderNumber);
            _requestLogService.LogWarning(
                "Printer Routing", $"No local print work for order {order.OrderNumber}",
                "The server route is not queued for this device; no output or acknowledgement was recorded.");
            return (false, KitchenPrintOutcome.NoWork, KitchenPrintOutcome.NoWork,
                KitchenPrintOutcome.NoWork);
        }

        var routedTargets = routeSelection.IsLegacy ? null : routeSelection.EligibleTargets;
        var cashierSuccess = await PrintCashierAsync(order, isManualPrint, cancellationToken, routedTargets);
        var kitchenOutcomes = await PrintKitchenTicketsAsync(
            order, config, policy, isManualPrint, routedTargets);
        var frontKitchenSuccess = kitchenOutcomes.Front;
        var backKitchenSuccess = kitchenOutcomes.Back;
        var generalDefaultSuccess = kitchenOutcomes.General;

        _logger.LogInformation("🖨️ Print complete for order {OrderNumber}: Cashier={C}, Front={F}, Back={B}, General/Default={G}",
            order.OrderNumber, cashierSuccess, frontKitchenSuccess, backKitchenSuccess, generalDefaultSuccess);

        return (cashierSuccess, frontKitchenSuccess, backKitchenSuccess, generalDefaultSuccess);
    }

    private async Task<bool> PrintCashierAsync(
        Order order,
        bool isManualPrint,
        CancellationToken cancellationToken,
        IReadOnlySet<DevicePrintTarget>? routedTargets)
    {
        if (routedTargets is not null && !routedTargets.Contains(DevicePrintTarget.Cashier))
            return false;

        try
        {
            var success = await PrintOrderAsync(order, PrinterType.Cashier, isManualPrint, cancellationToken);
            _logger.LogInformation("Cashier print: {Result}", success ? "✓" : "✗");
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing to cashier");
            return false;
        }
    }

    private async Task<(KitchenPrintOutcome Front, KitchenPrintOutcome Back, KitchenPrintOutcome General)>
        PrintKitchenTicketsAsync(
            Order order,
            PrinterConfiguration config,
            KitchenRoutingPolicy policy,
            bool isManualPrint,
            IReadOnlySet<DevicePrintTarget>? routedTargets)
    {
        if (!_marketplace.CanPrint(order, PrinterType.Kitchen))
            return (KitchenPrintOutcome.Unknown, KitchenPrintOutcome.Unknown, KitchenPrintOutcome.Unknown);

        var front = KitchenPrintOutcome.Sent;
        var back = KitchenPrintOutcome.Sent;
        var general = KitchenPrintOutcome.Sent;

        if (policy.Mode == KitchenRoutingMode.SingleKitchen)
        {
            if (AllowsGeneralOrDefault(routedTargets))
            {
                var selection = KitchenTicketFilter.SelectionForDestination(
                    order.Items, policy, KitchenTicketDestination.General);
                general = await PrintGeneralOrDefaultTicketAsync(
                    order.WithItems(selection.Items.ToList()), config, isManualPrint,
                    "Kitchen", selection.HasUnknown,
                    routedTargets?.Contains(DevicePrintTarget.General) == true);
            }
        }
        else
        {
            (front, back) = await PrintStationTicketsAsync(order, config, isManualPrint, routedTargets);
            if (AllowsGeneralOrDefault(routedTargets))
            {
                var selection = KitchenTicketFilter.SelectionForDestination(
                    order.Items, policy, KitchenTicketDestination.Default);
                general = await PrintGeneralOrDefaultTicketAsync(
                    order.WithItems(selection.Items.ToList()), config, isManualPrint,
                    "Default Kitchen", selection.HasUnknown,
                    routedTargets?.Contains(DevicePrintTarget.Default) == true);
            }
        }

        return (front, back, general);
    }

    /// <summary>
    /// Prints one additive UPDATE job. Unlike a legacy order receipt this is exactly one copy on the
    /// resolved General/Default kitchen destination. Failed, unknown and unconfigured outcomes are
    /// typed so the pipeline can retain the job instead of acknowledging a missing note as sent.
    /// </summary>
    public async Task<KitchenPrintOutcome> PrintUpdateAsync(
        PrinterFeedUpdate update,
        Func<CancellationToken, Task<PrinterUpdateAuthorizationResult>> authorizeImmediatelyBeforeSend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(authorizeImmediatelyBeforeSend);
        var validationError = PrinterFeedUpdateValidation.Validate(update);
        if (validationError is not null)
        {
            _logger.LogWarning("Rejecting invalid update job {JobId}: {Reason}", update.JobId, validationError);
            return KitchenPrintOutcome.Unknown;
        }

        var config = await _printerService.LoadConfigurationAsync();
        if (!UpdateJobRouting.AutoPrintEnabled(update.Target, config))
        {
            _logger.LogInformation("Automatic printing is disabled for update job {JobId} at {Target}",
                update.JobId, update.Target);
            return KitchenPrintOutcome.Skipped;
        }

        var destination = UpdateJobRouting.Resolve(update.Target, config);
        if (!destination.IsConfigured || destination.PrinterName is not { Length: > 0 } printerName)
        {
            _logger.LogWarning("No destination configured for update job {JobId}", update.JobId);
            _requestLogService.LogPrintResponse(
                "Update", update.OrderNumber, false, "No kitchen destination configured");
            return KitchenPrintOutcome.NotConfigured;
        }

        var content = UpdateReceiptComposer.Compose(update);
        PrinterUpdateAuthorizationResult authorization;
        try
        {
            authorization = await authorizeImmediatelyBeforeSend(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Authorization check failed immediately before update {JobId}; no printer bytes were sent",
                update.JobId);
            return KitchenPrintOutcome.Failed;
        }

        if (authorization.Status != PrinterUpdateAuthorizationStatus.Authorized)
        {
            _logger.LogInformation("Update job {JobId} was not authorized for output: {Status}",
                update.JobId, authorization.Status);
            return authorization.Status == PrinterUpdateAuthorizationStatus.Withdrawn
                ? KitchenPrintOutcome.Skipped : KitchenPrintOutcome.Failed;
        }

        var outcome = await _printerOutputService.SendAsync(printerName, content, cancellationToken);
        _logger.LogInformation("Update job {JobId} to {Printer}: {Result}",
            update.JobId, destination.PrinterName, outcome);
        return outcome;
    }

    /// <summary>
    /// The legacy station path, byte-for-byte: the Front ticket and the Back ticket exactly as they
    /// printed before the routing policy existed. Only the Stations policy calls this.
    /// </summary>
    private async Task<(KitchenPrintOutcome Front, KitchenPrintOutcome Back)> PrintStationTicketsAsync(
        Order order,
        PrinterConfiguration config,
        bool isManualPrint,
        IReadOnlySet<DevicePrintTarget>? routedTargets = null)
    {
        var policy = KitchenRoutingPolicy.Stations;
        var frontSelection = KitchenTicketFilter.SelectionForDestination(
            order.Items, policy, KitchenTicketDestination.FrontKitchen);
        var backSelection = KitchenTicketFilter.SelectionForDestination(
            order.Items, policy, KitchenTicketDestination.BackKitchen);
        var frontKitchenOrder = order.WithItems(frontSelection.Items.ToList());
        var backKitchenOrder = order.WithItems(backSelection.Items.ToList());
        var hasFrontKitchenItems = frontKitchenOrder.Items.Count > 0;
        var hasBackKitchenItems = backKitchenOrder.Items.Count > 0;

        var frontKitchenSuccess = KitchenPrintOutcome.Failed;
        var backKitchenSuccess = KitchenPrintOutcome.Failed;

        // Print to FrontKitchen if there are FrontKitchen items
        if (routedTargets is not null && !routedTargets.Contains(DevicePrintTarget.FrontKitchen))
        {
            frontKitchenSuccess = KitchenPrintOutcome.Sent;
        }
        else if (hasFrontKitchenItems)
        {
            try
            {
                // Logic: Use dedicated FrontPrinter if exists, otherwise fallback to CashierPrinter (NOT KitchenPrinter/BackPrinter)
                var frontKitchenPrinter = !string.IsNullOrWhiteSpace(config.FrontKitchenPrinterName)
                    ? config.FrontKitchenPrinterName
                    : config.CashierPrinterName; // Fallback to Cashier Printer as requested

                // Manual reprints bypass the auto-print toggle (consistent with PrintOrderAsync).
                // An intentionally-skipped print (auto-print off) is a success, not a failure — only
                // a genuine print attempt that fails reports false.
                bool shouldPrintFront = config.FrontKitchenAutoPrint || isManualPrint;
                frontKitchenSuccess = !shouldPrintFront;
                if (shouldPrintFront && !string.IsNullOrWhiteSpace(frontKitchenPrinter))
                {
                    var content = FormatKitchenReceipt(
                        frontKitchenOrder, config, config.FrontKitchenPaperWidth, "FRONT KITCHEN");
                    frontKitchenSuccess = await PrintRawContentAsync(frontKitchenPrinter, content);
                    _logger.LogInformation("FrontKitchen print ({ItemCount} items) to {Printer}: {Result}",
                        frontKitchenOrder.Items.Count, frontKitchenPrinter, frontKitchenSuccess ? "✓" : "✗");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error printing to FrontKitchen");
            }
        }
        else
        {
            frontKitchenSuccess = EmptyStationOutcome(
                routedTargets is not null && routedTargets.Contains(DevicePrintTarget.FrontKitchen),
                frontSelection.HasUnknown); // No known items to print
        }

        // Keep any known leaf work that can be routed, but surface an unknown sibling instead of
        // silently claiming the whole station succeeded.
        if (frontSelection.HasUnknown)
            frontKitchenSuccess = KitchenPrintOutcome.Unknown;

        // Print to BackKitchen if there are BackKitchen items
        if (routedTargets is not null && !routedTargets.Contains(DevicePrintTarget.BackKitchen))
        {
            backKitchenSuccess = KitchenPrintOutcome.Sent;
        }
        else if (hasBackKitchenItems)
        {
            try
            {
                var backKitchenPrinter = !string.IsNullOrWhiteSpace(config.BackKitchenPrinterName)
                    ? config.BackKitchenPrinterName
                    : config.KitchenPrinterName;  // Fallback to legacy

                // Manual reprints bypass the auto-print toggle (consistent with PrintOrderAsync).
                // An intentionally-skipped print (auto-print off) is a success, not a failure.
                bool shouldPrintBack = config.BackKitchenAutoPrint || isManualPrint;
                backKitchenSuccess = !shouldPrintBack;
                if (shouldPrintBack && !string.IsNullOrWhiteSpace(backKitchenPrinter))
                {
                    var content = FormatKitchenReceipt(
                        backKitchenOrder, config, config.BackKitchenPaperWidth, "Back Kitchen");
                    backKitchenSuccess = await PrintRawContentAsync(backKitchenPrinter, content);
                    _logger.LogInformation("BackKitchen print ({ItemCount} items): {Result}",
                        backKitchenOrder.Items.Count, backKitchenSuccess ? "✓" : "✗");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error printing to BackKitchen");
            }
        }
        else
        {
            backKitchenSuccess = EmptyStationOutcome(
                routedTargets is not null && routedTargets.Contains(DevicePrintTarget.BackKitchen),
                backSelection.HasUnknown); // No known items to print
        }

        if (backSelection.HasUnknown)
            backKitchenSuccess = KitchenPrintOutcome.Unknown;

        return (frontKitchenSuccess, backKitchenSuccess);
    }

    private static KitchenPrintOutcome EmptyStationOutcome(bool routeAssigned, bool hasUnknown) =>
        routeAssigned || hasUnknown ? KitchenPrintOutcome.Unknown : KitchenPrintOutcome.Sent;

    /// <summary>
    /// Composes and sends the ONE General (SingleKitchen) or Default (Stations) kitchen ticket
    /// through the shared transport path, to the destination
    /// <see cref="KitchenDestinationResolver"/> picks over the saved configuration. An empty
    /// selection owes nothing and is trivially satisfied; work with no resolvable destination is
    /// <see cref="KitchenPrintStatus.NotConfigured"/> — the kitchen never sees a ticket, so it is
    /// neither printed nor success (issue #113 C02: this used to collapse into a silent true).
    /// </summary>
    private async Task<KitchenPrintOutcome> PrintGeneralOrDefaultTicketAsync(
        Order filtered,
        PrinterConfiguration config,
        bool isManualPrint,
        string header,
        bool hasUnknown = false,
        bool routeAssigned = false)
    {
        if (filtered.Items.Count == 0)
        {
            return hasUnknown || routeAssigned ? KitchenPrintOutcome.Unknown : KitchenPrintOutcome.Sent;
        }

        // Manual reprints bypass the auto-print toggle (consistent with the Front/Back path).
        // An intentionally-skipped print (auto-print off) is a success, not a failure — checked
        // before resolution, exactly how Front/Back skip.
        if (!isManualPrint && !config.KitchenAutoPrint)
        {
            _logger.LogInformation("Auto-print disabled for {Header}", header);
            return hasUnknown ? KitchenPrintOutcome.Unknown : KitchenPrintOutcome.Skipped;
        }

        var resolution = KitchenDestinationResolver.Resolve(new KitchenDestinationSettings(
            config.DefaultKitchenPrinterName,
            config.KitchenPrinterName,
            config.FrontKitchenPrinterName,
            config.BackKitchenPrinterName));

        if (!resolution.IsConfigured || resolution.PrinterName is not { Length: > 0 } printerName)
        {
            _logger.LogWarning(
                "No resolvable {Header} destination for order {OrderNumber}: {ItemCount} items stay pending configuration",
                header, filtered.OrderNumber, filtered.Items.Count);
            _requestLogService.LogPrintResponse(
                header, filtered.OrderNumber, false, "No kitchen destination configured");
            return hasUnknown ? KitchenPrintOutcome.Unknown : KitchenPrintOutcome.NotConfigured;
        }

        var content = FormatKitchenReceipt(filtered, config, config.KitchenPaperWidth, header);
        var sent = await PrintRawContentAsync(printerName, content);
        _logger.LogInformation("{Header} print ({ItemCount} items) to {Printer}: {Result}",
            header, filtered.Items.Count, resolution.PrinterName, sent ? "✓" : "✗");
        if (hasUnknown)
            return KitchenPrintOutcome.Unknown;
        return sent ? KitchenPrintOutcome.Sent : KitchenPrintOutcome.Failed;
    }

    /// <summary>
    /// A copy of the order carrying only what <paramref name="kitchenType"/> is responsible for.
    /// The selection itself is <see cref="KitchenTicketFilter"/>: it recurses into
    /// <see cref="OrderItem.SideItems"/>, because since backend PR #237 that is where a bundle's
    /// components live.
    /// </summary>
    private static Order CreateFilteredOrder(Order original, string kitchenType) =>
        original.WithItems(KitchenTicketFilter.ItemsForKitchen(original.Items, kitchenType));

    private static bool AllowsGeneralOrDefault(IReadOnlySet<DevicePrintTarget>? routedTargets) =>
        routedTargets is null
        || routedTargets.Contains(DevicePrintTarget.General)
        || routedTargets.Contains(DevicePrintTarget.Default);

    public async Task<bool> PrintOrderAsync(Order order, PrinterType printerType, bool isManualPrint = false, CancellationToken cancellationToken = default)
    {
        if (!_marketplace.CanPrint(order, printerType)) return false;
        try
        {
            var config = await _printerService.LoadConfigurationAsync();

            string printerName;
            int copies;
            bool autoPrint;
            int paperWidth;

            if (printerType == PrinterType.Kitchen)
            {
                printerName = config.KitchenPrinterName;
                copies = config.KitchenPrintCopies;
                autoPrint = config.KitchenAutoPrint;
                paperWidth = config.KitchenPaperWidth;
            }
            else
            {
                printerName = config.CashierPrinterName;
                copies = config.CashierPrintCopies;
                autoPrint = config.CashierAutoPrint;
                paperWidth = config.CashierPaperWidth;
            }

            // Check auto-print settings (only for automatic printing)
            if (!isManualPrint && !autoPrint)
            {
                _logger.LogInformation("Auto-print disabled for {PrinterType}", printerType);
                return true;
            }

            // Check time restrictions (only for automatic printing)
            if (!isManualPrint && config.EnableTimeRestriction)
            {
                var now = DateTime.Now.TimeOfDay;
                bool isInRestrictedTime = false;

                if (config.RestrictStartTime < config.RestrictEndTime)
                {
                    // Normal case: e.g., 12:00 - 13:00
                    isInRestrictedTime = now >= config.RestrictStartTime && now < config.RestrictEndTime;
                }
                else
                {
                    // Overnight case: e.g., 23:00 - 01:00
                    isInRestrictedTime = now >= config.RestrictStartTime || now < config.RestrictEndTime;
                }

                if (isInRestrictedTime)
                {
                    _logger.LogInformation("Auto-print skipped for {PrinterType} - current time {Time} is within restricted period {Start}-{End}",
                        printerType, now.ToString(@"hh\:mm"), config.RestrictStartTime.ToString(@"hh\:mm"), config.RestrictEndTime.ToString(@"hh\:mm"));
                    return true; // Return true to indicate no error, just skipped
                }
            }

            if (string.IsNullOrWhiteSpace(printerName))
            {
                _logger.LogWarning("No printer configured for {PrinterType}", printerType);
                _requestLogService.LogPrintResponse(printerType.ToString(), order.OrderNumber, false, "No printer configured");
                return false;
            }

            // Cashier printer only prints the bill (no duplicate kitchen format)
            // Kitchen tickets are handled by dedicated kitchen printers in PrintOrderToAllPrintersAsync
            bool success = true;
            // Print the appropriate format
            string content = printerType == PrinterType.Kitchen
                ? FormatKitchenReceipt(order, config, paperWidth)
                : FormatCashierReceipt(order, config, paperWidth);

            // Log print request with full content
            _requestLogService.LogPrintRequest(printerType.ToString(), order.OrderNumber, printerName, content);

            for (int i = 0; i < copies; i++)
            {
                var result = await PrintRawContentAsync(printerName, content);
                success = success && result;

                if (i < copies - 1)
                {
                    await Task.Delay(500, cancellationToken); // Small delay between copies
                }
            }

            // Log print response
            if (success)
            {
                _logger.LogInformation("Successfully printed order #{OrderNumber} for {Table} to {PrinterType} printer ({Copies} copies)",
                    order.OrderNumber,
                    TableDisplay.ResolveLabel(order.TableLabel, order.TableNumber) ?? "no table",
                    printerType,
                    copies);
                _requestLogService.LogPrintResponse(printerType.ToString(), order.OrderNumber, true, $"Printed {copies} {(copies > 1 ? "copies" : "copy")}");
            }
            else
            {
                _requestLogService.LogPrintResponse(printerType.ToString(), order.OrderNumber, false, "Print operation failed");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing order #{OrderNumber} to {PrinterType}", order.OrderNumber, printerType);
            _requestLogService.LogPrintResponse(printerType.ToString(), order.OrderNumber, false, $"Exception: {ex.Message}");
            return false;
        }
    }

    private string FormatKitchenReceipt(Order order, PrinterConfiguration config, int paperWidth, string? kitchenName = null)
    {
        var sb = new StringBuilder();
        var language = PrintLanguagePolicy.Resolve(config.PrintLanguage, order.PreferredLanguage);
        var labels = PrintLabelCatalog.For(language);
        var styles = CurrentStyles();

        // Initialize printer and set Turkish code page for character support
        sb.Append(EscPosCommands.Initialize);
        sb.Append(EscPosCommands.CodepageTurkish);

        // KITCHEN NAME HEADER — styled by the settings page (Kitchen Header section)
        if (!string.IsNullOrEmpty(kitchenName))
        {
            sb.Append(ApplyStyle(styles.KitchenHeader));
            sb.AppendLine($"*** {kitchenName} ***");
            sb.Append(ResetStyle(styles.KitchenHeader));
            sb.AppendLine();
        }

        // Order number + Date/Time (Kitchen Order Info section)
        sb.Append(ApplyStyle(styles.KitchenOrderInfo));
        var localTime = order.OrderDate.Kind == DateTimeKind.Utc
            ? order.OrderDate.ToLocalTime()
            : order.OrderDate;
        sb.AppendLine($"{order.OrderNumber} - {localTime:dd/MM/yyyy HH:mm}");
        sb.Append(ResetStyle(styles.KitchenOrderInfo));

        // Type + Table (Kitchen Order Type section; defaults to Tall)
        sb.Append(ApplyStyle(styles.KitchenOrderType));
        ReceiptComposer.AppendTypeAndTableLine(sb, order, labels);
        AmendmentReceiptComposer.AppendKitchenIdentity(sb, order, language);
        _marketplace.AppendIdentity(sb, order, language, showPayment: false);
        sb.Append(ResetStyle(styles.KitchenOrderType));

        // Customer name only (no phone) — styled as part of the order info block
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
        {
            sb.Append(ApplyStyle(styles.KitchenOrderInfo));
            sb.AppendLine($"{labels.Customer}: {order.CustomerName}");
            sb.Append(ResetStyle(styles.KitchenOrderInfo));
        }

        // Order-level notes — the kitchen reads them at the top, never buried under the items.
        sb.Append(ApplyStyle(styles.KitchenIngredients));
        ReceiptComposer.AppendOrderNotesLine(sb, order, labels);
        sb.Append(ResetStyle(styles.KitchenIngredients));

        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Log item count for debugging
        _logger.LogInformation("Printing {ItemCount} items for order {OrderNumber}", order.Items?.Count ?? 0, order.OrderNumber);

        if (order.Items != null && order.Items.Any())
        {
            foreach (var item in order.Items)
            {
                ReceiptComposer.AppendKitchenItemLines(sb, item, depth: 0, labels, styles: styles);
                sb.AppendLine();
            }
        }
        else
        {
            // No items found - log warning
            _logger.LogWarning("No items found in order {OrderNumber}", order.OrderNumber);
            sb.AppendLine(labels.NoItems);
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();

        // Feed extra lines before cut to prevent text cutoff
        sb.Append(EscPosCommands.Feed5Lines);
        sb.Append(EscPosCommands.FullCut);

        return sb.ToString();
    }

    private string FormatCashierReceipt(Order order, PrinterConfiguration config, int paperWidth)
    {
        var sb = new StringBuilder();
        var language = PrintLanguagePolicy.Resolve(config.PrintLanguage, order.PreferredLanguage);
        var labels = PrintLabelCatalog.For(language);
        var styles = CurrentStyles();
        var currency = _marketplace.Currency(order);

        // Initialize printer and set Turkish code page for character support
        // Some printers need the code page command repeated to properly switch encoding
        sb.Append(EscPosCommands.Initialize);
        sb.Append(EscPosCommands.CodepageTurkish);
        sb.Append(EscPosCommands.CodepageTurkish); // Send twice for stubborn printers

        // Header (Cashier Header section — defaults: double size, bold, emphasized, centered)
        sb.Append(ApplyStyle(styles.CashierHeader));
        sb.AppendLine($"{config.RestaurantName}");
        sb.AppendLine(labels.OnlineOrder);
        sb.Append(ResetStyle(styles.CashierHeader));
        sb.AppendLine();

        // Convert to local time if needed
        var localTime = order.OrderDate.Kind == DateTimeKind.Utc
            ? order.OrderDate.ToLocalTime()
            : order.OrderDate;

        // Order number + Date/Time on one line (Cashier Order Info section)
        sb.Append(ApplyStyle(styles.CashierOrderInfo));
        sb.AppendLine($"{order.OrderNumber} - {localTime:dd/MM/yyyy HH:mm}");
        sb.Append(ResetStyle(styles.CashierOrderInfo));

        // Type + Table on one line
        sb.Append(ApplyStyle(styles.CashierOrderInfo));
        ReceiptComposer.AppendTypeAndTableLine(sb, order, labels);
        _marketplace.AppendIdentity(sb, order, language, showPayment: true);
        sb.Append(ResetStyle(styles.CashierOrderInfo));

        // Customer name, plus a phone number for orders someone may need to call about
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
        {
            sb.Append(ApplyStyle(styles.CashierOrderInfo));
            sb.AppendLine($"{labels.Customer}: {order.CustomerName}");
            sb.Append(ResetStyle(styles.CashierOrderInfo));
        }

        if (order.Type == "Delivery")
        {
            var phone = order.DeliveryAddress?.Phone;
            if (string.IsNullOrWhiteSpace(phone))
            {
                phone = order.CustomerPhone;
            }
            if (!string.IsNullOrWhiteSpace(phone))
            {
                sb.AppendLine($"{labels.Tel}: {phone}");
            }
        }

        // Order-level notes (e.g. "ring the doorbell") — printed before the items so they are not
        // lost below a long list.
        sb.Append(EscPosCommands.ExtraDarkOn);
        ReceiptComposer.AppendOrderNotesLine(sb, order, labels);
        sb.Append(EscPosCommands.ExtraDarkOff);

        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Items with prices. Each top-level line carries its customizations, its special
        // instruction and its bundle components/side items (no price on those — the parent total
        // already covers them), so the paper matches what the guest actually ordered.
        if (order.Items != null && order.Items.Any())
        {
            var spacing = paperWidth == 80 ? 48 : 32;
            foreach (var item in order.Items)
            {
                sb.Append(ApplyStyle(styles.CashierItemLine));
                ReceiptComposer.AppendCashierItemLines(sb, item, depth: 0, spacing, labels, currency: currency);
                sb.Append(ResetStyle(styles.CashierItemLine));
            }
        }
        else
        {
            _logger.LogWarning("No items found in cashier receipt for order {OrderNumber}", order.OrderNumber);
            sb.AppendLine(labels.NoItems);
        }

        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Subtotal, Tax, Discounts, Delivery Fee, Tip (Cashier Totals section)
        sb.Append(ApplyStyle(styles.CashierTotals));
        sb.AppendLine($"{labels.Subtotal}: {ReceiptComposer.Money(order.SubTotal, currency)}");

        _marketplace.AppendTax(sb, order, labels, language);

        if (order.Discount > 0)
        {
            sb.AppendLine($"{labels.Discount} ({order.DiscountPercentage.ToString(CultureInfo.InvariantCulture)}%): -{ReceiptComposer.Money(order.Discount, currency)}");
        }

        // Customer-specific discount money is SEPARATE from Discount (backend OrderPricingService:
        // sale = items + fee - Discount - CustomerDiscountAmount). Without this line the printed
        // breakdown does not reconcile against the total whenever it applied.
        if (order.CustomerDiscountAmount > 0)
        {
            sb.AppendLine($"{labels.CustomerDiscount}: -{ReceiptComposer.Money(order.CustomerDiscountAmount, currency)}");
        }

        if (!string.IsNullOrWhiteSpace(order.PromoCode))
        {
            sb.AppendLine($"{labels.Promo}: {order.PromoCode}");
        }

        if (order.DeliveryFee > 0)
        {
            sb.AppendLine($"{labels.DeliveryFee}: {ReceiptComposer.Money(order.DeliveryFee, currency)}");
        }

        if (order.Tip > 0)
        {
            sb.AppendLine($"{labels.Tip}: {ReceiptComposer.Money(order.Tip, currency)}");
        }

        sb.Append(ResetStyle(styles.CashierTotals));
        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Total (Cashier Grand Total section — defaults: double size, bold, emphasized)
        sb.Append(ApplyStyle(styles.CashierGrandTotal));
        sb.AppendLine($"{labels.Total}: {ReceiptComposer.Money(order.Total, currency)}");
        sb.Append(ResetStyle(styles.CashierGrandTotal));
        sb.AppendLine();

        // Staff tips are collected outside order debt; guest Order.Tip is already in Total.
        var paymentTip = order.PaymentTipMinor / MinorUnitsPerCurrencyUnit;
        if (paymentTip > 0)
        {
            sb.AppendLine($"{labels.PaymentTip}: {ReceiptComposer.Money(paymentTip, currency)}");
        }

        // Collected money includes net staff tips while Due remains food/order debt only.
        if (order.TotalPaid + paymentTip > 0)
        {
            sb.AppendLine($"{labels.Paid}: {ReceiptComposer.Money(order.TotalPaid + paymentTip, currency)}");
        }

        if (order.ExternalOrder is null && order.RemainingAmount > 0)
        {
            sb.Append(EscPosCommands.ExtraDarkOn);
            sb.AppendLine($"{labels.Due}: {ReceiptComposer.Money(order.RemainingAmount, currency)}");
            sb.Append(EscPosCommands.ExtraDarkOff);
        }

        sb.AppendLine();

        // Payment information
        if (order.Payments != null && order.Payments.Any())
        {
            sb.Append(EscPosCommands.ExtraDarkOn);
            sb.AppendLine($"{labels.Payment}:");
            foreach (var payment in order.Payments)
            {
                var paymentLabel = order.ExternalOrder is { } source
                    ? _marketplace.ProviderName(source)
                    : labels.PaymentMethodLabel(payment.PaymentMethod);
                var method = string.IsNullOrWhiteSpace(payment.CardLastFourDigits)
                    ? paymentLabel
                    : $"{paymentLabel} *{payment.CardLastFourDigits}";
                var collected = payment.Amount - (payment.RefundedAmount ?? 0)
                    + (payment.TipMinor - payment.RefundedTipMinor) / MinorUnitsPerCurrencyUnit;
                sb.AppendLine($"{method}: {ReceiptComposer.Money(collected, order.ExternalOrder is null ? currency ?? payment.Currency : currency)}");
            }
            sb.Append(EscPosCommands.ExtraDarkOff);
            sb.AppendLine();
        }

        // Delivery address, contact phone and courier instructions for delivery orders
        var address = order.DeliveryAddress;
        if (order.Type == "Delivery" && address is not null && !string.IsNullOrWhiteSpace(address.FullAddress))
        {
            sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));
            sb.Append(EscPosCommands.ExtraDarkOn);
            sb.AppendLine(labels.DeliveryTo);
            sb.AppendLine(address.FullAddress);
            if (!string.IsNullOrWhiteSpace(address.DeliveryInstructions))
            {
                sb.AppendLine($"{labels.Instructions}: {address.DeliveryInstructions}");
            }
            sb.Append(EscPosCommands.ExtraDarkOff);
            sb.AppendLine();
        }

        // Footer
        sb.AppendLine(new string('=', paperWidth == 80 ? 48 : 32));
        sb.Append(EscPosCommands.AlignCenter);
        sb.AppendLine(labels.ThankYou);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();

        // Feed extra lines before cut to prevent text cutoff
        sb.Append(EscPosCommands.Feed5Lines);
        sb.Append(EscPosCommands.FullCut);

        return sb.ToString();
    }

    /// <summary>
    /// Sends the composed ESC/POS content to the configured printer through the
    /// <see cref="IPrinterTransport"/> seam (ADR-006 Phase 2b): an IP-literal target goes over
    /// <see cref="NetworkTcpTransport"/> (the print path on Android and any network-attached
    /// printer), anything else is a Windows spooler name sent through
    /// <see cref="WindowsSpoolerTransport"/> — the winspool.drv RAW write that used to live inline
    /// here. Same bytes on the wire and the same bool semantics as before (any failure → false;
    /// non-Windows spooler sends fail via <see cref="PlatformNotSupportedException"/>).
    /// </summary>
    private async Task<bool> PrintRawContentAsync(string printerName, string content)
    {
        var outcome = await _printerOutputService.SendAsync(printerName, content);
        return outcome.IsSuccess;
    }
}
