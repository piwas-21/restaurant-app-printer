using System.Text;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public class OrderPrintService : IOrderPrintService
{
    private readonly IPrinterService _printerService;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<OrderPrintService> _logger;
    private readonly PrintStyleSettingsService _styleService;
    private readonly PrintStyleSettings _styleSettings;

    // ESC/POS Commands for MAXIMUM darkness printing
    private const string ESC_INIT = "\x1B\x40"; // Initialize printer
    private const string ESC_BOLD_ON = "\x1B\x45\x01"; // Bold on
    private const string ESC_BOLD_OFF = "\x1B\x45\x00"; // Bold off
    private const string ESC_EMPHASIZED_ON = "\x1B\x47\x01"; // Emphasized/Double-strike on
    private const string ESC_EMPHASIZED_OFF = "\x1B\x47\x00"; // Emphasized off
    private const string ESC_SIZE_NORMAL = "\x1D\x21\x00"; // Normal size (1x width, 1x height)
    private const string ESC_SIZE_TALL = "\x1D\x21\x01"; // Tall only (1x width, 2x height)
    private const string ESC_SIZE_WIDE = "\x1D\x21\x10"; // Wide only (2x width, 1x height)
    private const string ESC_DOUBLE_ON = "\x1D\x21\x11"; // Double width and height (2x, 2x)
    private const string ESC_DOUBLE_OFF = "\x1D\x21\x00"; // Normal size
    private const string ESC_LARGE_ON = "\x1D\x21\x22"; // 2x width, 3x height (larger size for kitchen)
    private const string ESC_ALIGN_CENTER = "\x1B\x61\x01"; // Center align
    private const string ESC_ALIGN_LEFT = "\x1B\x61\x00"; // Left align
    private const string ESC_CUT = "\x1D\x56\x00"; // Full cut
    private const string ESC_PARTIAL_CUT = "\x1D\x56\x01"; // Partial cut
    private const string ESC_FEED_AND_CUT = "\x1B\x64\x03"; // Feed 3 lines and cut
    private const string ESC_CODEPAGE_TURKISH = "\x1B\x74\x09"; // Set PC857 code page (Turkish MS-DOS - supports Turkish + Western European)

    // Combined commands for MAXIMUM darkness
    private const string EXTRA_DARK_ON = ESC_BOLD_ON + ESC_EMPHASIZED_ON; // Bold + Emphasized for maximum darkness
    private const string EXTRA_DARK_OFF = ESC_BOLD_OFF + ESC_EMPHASIZED_OFF; // Turn off all emphasis
    private const string ESC_FEED_LINES = "\x1B\x64\x05"; // Feed 5 lines before cut

    // KitchenType values as the backend emits them (OrderItemDto.KitchenType).
    private const string FRONT_KITCHEN = "FrontKitchen";
    private const string BACK_KITCHEN = "BackKitchen";

    public OrderPrintService(
        IPrinterService printerService,
        IRequestLogService requestLogService,
        ILogger<OrderPrintService> logger,
        IAppDataPathProvider pathProvider)
    {
        _printerService = printerService;
        _requestLogService = requestLogService;
        _logger = logger;
        // Path provider injected (was `new PrintStyleSettingsService()` with a MAUI default) so this
        // service is source-linkable into the headless print-to-sink test.
        _styleService = new PrintStyleSettingsService(pathProvider);
        _styleSettings = _styleService.LoadSettings();
    }

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
    /// Prints order to all appropriate printers: Cashier + FrontKitchen + BackKitchen (filtered by item KitchenType)
    /// </summary>
    public async Task<(bool Cashier, bool FrontKitchen, bool BackKitchen)> PrintOrderToAllPrintersAsync(
        Order order,
        bool isManualPrint = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🖨️ Printing order {OrderNumber} to all printers", order.OrderNumber);

        var config = await _printerService.LoadConfigurationAsync();
        bool cashierSuccess = false;
        bool frontKitchenSuccess = false;
        bool backKitchenSuccess = false;

        // 1. ALWAYS print to Cashier (full receipt with prices)
        try
        {
            cashierSuccess = await PrintOrderAsync(order, PrinterType.Cashier, isManualPrint, cancellationToken);
            _logger.LogInformation("Cashier print: {Result}", cashierSuccess ? "✓" : "✗");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing to cashier");
        }

        // 2. Route the item tree to each kitchen.
        //
        // Both tickets are built up front so that "does this kitchen get a ticket at all" is
        // answered by the very thing that will be printed — the two used to be decided separately,
        // and separately is how they drifted apart. Since backend PR #237 (issue #234) made
        // OrderDto.Items root-only, a top-level scan misses every bundle component: a FrontKitchen
        // combo containing BackKitchen fries produced no back-kitchen ticket, and printed the fries
        // on the front kitchen's. KitchenTicketFilter walks the whole tree instead.
        var frontKitchenOrder = CreateFilteredOrder(order, FRONT_KITCHEN);
        var backKitchenOrder = CreateFilteredOrder(order, BACK_KITCHEN);
        var hasFrontKitchenItems = frontKitchenOrder.Items.Count > 0;
        var hasBackKitchenItems = backKitchenOrder.Items.Count > 0;

        // 3. Print to FrontKitchen if there are FrontKitchen items
        if (hasFrontKitchenItems)
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
            frontKitchenSuccess = true; // No items to print
        }

        // 4. Print to BackKitchen if there are BackKitchen items
        if (hasBackKitchenItems)
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
            backKitchenSuccess = true; // No items to print
        }

        _logger.LogInformation("🖨️ Print complete for order {OrderNumber}: Cashier={C}, Front={F}, Back={B}",
            order.OrderNumber, cashierSuccess, frontKitchenSuccess, backKitchenSuccess);

        return (cashierSuccess, frontKitchenSuccess, backKitchenSuccess);
    }

    /// <summary>
    /// A copy of the order carrying only what <paramref name="kitchenType"/> is responsible for.
    /// The selection itself is <see cref="KitchenTicketFilter"/>: it recurses into
    /// <see cref="OrderItem.SideItems"/>, because since backend PR #237 that is where a bundle's
    /// components live.
    /// </summary>
    private Order CreateFilteredOrder(Order original, string kitchenType) =>
        original.WithItems(KitchenTicketFilter.ItemsForKitchen(original.Items, kitchenType));

    public async Task<bool> PrintOrderAsync(Order order, PrinterType printerType, bool isManualPrint = false, CancellationToken cancellationToken = default)
    {
        // Extract order number for logging (parse the numeric part)
        int orderNumForLog = int.TryParse(order.OrderNumber.Split('/').Last(), out var orderNumParsed) ? orderNumParsed : 0;

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
                _requestLogService.LogPrintResponse(printerType.ToString(), orderNumForLog, false, "No printer configured");
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
            _requestLogService.LogPrintRequest(printerType.ToString(), orderNumForLog, printerName, content);

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
                _logger.LogInformation("Successfully printed order #{OrderNumber} to {PrinterType} printer ({Copies} copies)",
                    order.OrderNumber, printerType, copies);
                _requestLogService.LogPrintResponse(printerType.ToString(), orderNumForLog, true, $"Printed {copies} {(copies > 1 ? "copies" : "copy")}");
            }
            else
            {
                _requestLogService.LogPrintResponse(printerType.ToString(), orderNumForLog, false, "Print operation failed");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing order #{OrderNumber} to {PrinterType}", order.OrderNumber, printerType);
            _requestLogService.LogPrintResponse(printerType.ToString(), orderNumForLog, false, $"Exception: {ex.Message}");
            return false;
        }
    }

    private string FormatKitchenReceipt(Order order, PrinterConfiguration config, int paperWidth, string? kitchenName = null)
    {
        var sb = new StringBuilder();

        // Initialize printer and set Turkish code page for character support
        sb.Append(ESC_INIT);
        sb.Append(ESC_CODEPAGE_TURKISH);

        // KITCHEN NAME HEADER - Normal size with bold (smaller than before)
        if (!string.IsNullOrEmpty(kitchenName))
        {
            sb.Append(ESC_ALIGN_CENTER);
            sb.Append(EXTRA_DARK_ON);
            sb.AppendLine($"*** {kitchenName} ***");
            sb.Append(EXTRA_DARK_OFF);
            sb.AppendLine();
        }

        // Order number + Date/Time - Normal size with bold (same as kitchen title)
        sb.Append(ESC_ALIGN_LEFT);
        sb.Append(EXTRA_DARK_ON);
        var localTime = order.OrderDate.Kind == DateTimeKind.Utc
            ? order.OrderDate.ToLocalTime()
            : order.OrderDate;
        sb.AppendLine($"{order.OrderNumber} - {localTime:dd/MM/yyyy HH:mm}");
        sb.Append(EXTRA_DARK_OFF);

        // Type + Table - TALL size (1x width, 2x height - intermediate between normal and double)
        sb.Append(ESC_SIZE_TALL);
        if (order.TableNumber.HasValue && order.TableNumber.Value > 0)
        {
            sb.AppendLine($"Type: {order.Type} - Table {order.TableNumber}");
        }
        else
        {
            sb.AppendLine($"Type: {order.Type}");
        }
        sb.Append(ESC_SIZE_NORMAL);

        // Customer name only (no phone)
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
        {
            sb.Append(EXTRA_DARK_ON);
            sb.AppendLine($"Customer: {order.CustomerName}");
            sb.Append(EXTRA_DARK_OFF);
        }


        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Log item count for debugging
        _logger.LogInformation("Printing {ItemCount} items for order {OrderNumber}", order.Items?.Count ?? 0, order.OrderNumber);

        if (order.Items != null && order.Items.Any())
        {
            foreach (var item in order.Items)
            {
                // Log each item for debugging
                _logger.LogInformation("Item: {Quantity}x {ProductName}", item.Quantity, item.ProductName);

                AppendKitchenItem(sb, item, depth: 0);
                sb.AppendLine();
            }
        }
        else
        {
            // No items found - log warning
            _logger.LogWarning("No items found in order {OrderNumber}", order.OrderNumber);
            sb.AppendLine("(No items in order)");
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();

        // Feed extra lines before cut to prevent text cutoff
        sb.Append(ESC_FEED_LINES);
        sb.Append(ESC_CUT);

        return sb.ToString();
    }

    /// <summary>
    /// Writes one line of the kitchen ticket and everything hanging off it. Recursive because a
    /// bundle nests to arbitrary depth since backend PR #237 — the previous one-level-deep render
    /// showed a grandchild nowhere, and showed a child's name and quantity but never its "NO onion"
    /// or its note (both of which used to print, back when every child was its own top-level row).
    /// </summary>
    private void AppendKitchenItem(StringBuilder sb, OrderItem item, int depth)
    {
        var indent = new string(' ', depth * 3);

        // A line this kitchen does not make (KitchenTicketFilter kept it only to say what the
        // components below it belong to — a FrontKitchen combo on the back kitchen's ticket).
        // Parenthesised and left at normal size so it doesn't read as a dish to prepare; the
        // components under it are the work.
        if (item.IsContextOnly)
        {
            sb.AppendLine($"{indent}({item.Quantity}x {item.ProductName})");
        }
        else
        {
            // Item name and quantity - WIDE size (2x width, 1x height - bigger than normal).
            // "+ " marks a component/add-on so it still reads as part of the line above it.
            sb.Append(ESC_SIZE_WIDE);
            sb.AppendLine(depth == 0
                ? $"{item.Quantity}x {item.ProductName}"
                : $"{indent}+ {item.Quantity}x {item.ProductName}");
            sb.Append(ESC_SIZE_NORMAL);
        }

        // Show variation if available (normal size)
        if (!string.IsNullOrWhiteSpace(item.VariationName))
        {
            sb.AppendLine($"{indent}   - {item.VariationName}");
        }

        // Show ingredient customizations - ONLY modified ingredients (removed or extra)
        var modifiedIngredients = item.IngredientCustomizations?
            .Where(ing => ing.IsRemoved || ing.Quantity > 1)
            .ToList();
        if (modifiedIngredients != null && modifiedIngredients.Any())
        {
            foreach (var ing in modifiedIngredients)
            {
                sb.Append(ESC_SIZE_TALL);
                if (ing.IsRemoved)
                {
                    // TALL size "NO" prefix for removed ingredients
                    sb.AppendLine($"{indent}   - NO {ing.IngredientName}");
                }
                else
                {
                    // TALL size "EXTRA" prefix for extra ingredients
                    sb.AppendLine($"{indent}   + EXTRA {ing.IngredientName}");
                }
                sb.Append(ESC_SIZE_NORMAL);
            }
        }

        // Show special instructions - TALL size for visibility. Before the nested components, not
        // after: trailing the children makes a parent's note read as if it belonged to the last
        // child printed.
        if (!string.IsNullOrWhiteSpace(item.SpecialInstructions))
        {
            sb.Append(ESC_SIZE_TALL);
            sb.AppendLine($"{indent}   NOTE: {item.SpecialInstructions}");
            sb.Append(ESC_SIZE_NORMAL);
        }

        // Show side items / additionals (bundle components and add-on sides)
        if (item.SideItems != null && item.SideItems.Any())
        {
            foreach (var side in item.SideItems)
            {
                AppendKitchenItem(sb, side, depth + 1);
            }
        }
    }

    private string FormatCashierReceipt(Order order, PrinterConfiguration config, int paperWidth)
    {
        var sb = new StringBuilder();

        // Initialize printer and set Turkish code page for character support
        // Some printers need the code page command repeated to properly switch encoding
        sb.Append(ESC_INIT);
        sb.Append(ESC_CODEPAGE_TURKISH);
        sb.Append(ESC_CODEPAGE_TURKISH); // Send twice for stubborn printers

        // Header - EXTRA DARK, Bold, and Double Size
        sb.Append(ESC_ALIGN_CENTER);
        sb.Append(ESC_DOUBLE_ON);
        sb.Append(EXTRA_DARK_ON);
        sb.AppendLine($"{config.RestaurantName}");
        sb.Append(ESC_DOUBLE_OFF);

        sb.AppendLine("ONLINE ORDER");
        sb.Append(EXTRA_DARK_OFF);
        sb.AppendLine();

        // Convert to local time if needed
        var localTime = order.OrderDate.Kind == DateTimeKind.Utc
            ? order.OrderDate.ToLocalTime()
            : order.OrderDate;

        // Order number + Date/Time on one line - EXTRA DARK for visibility
        sb.Append(ESC_ALIGN_LEFT);
        sb.Append(EXTRA_DARK_ON);
        sb.AppendLine($"{order.OrderNumber} - {localTime:dd/MM/yyyy HH:mm}");
        sb.Append(EXTRA_DARK_OFF);

        // Type + Table on one line
        sb.Append(EXTRA_DARK_ON);
        if (order.TableNumber.HasValue && order.TableNumber.Value > 0)
        {
            sb.AppendLine($"Type: {order.Type} - Table {order.TableNumber}");
        }
        else
        {
            sb.AppendLine($"Type: {order.Type}");
        }
        sb.Append(EXTRA_DARK_OFF);

        // Customer name only (no phone/email)
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
        {
            sb.Append(EXTRA_DARK_ON);
            sb.AppendLine($"Customer: {order.CustomerName}");
            sb.Append(EXTRA_DARK_OFF);
        }
        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Items with prices - simplified (no customizations)
        if (order.Items != null && order.Items.Any())
        {
            foreach (var item in order.Items)
            {
                var itemName = item.ProductName;
                if (!string.IsNullOrWhiteSpace(item.VariationName))
                {
                    itemName += $" ({item.VariationName})";
                }

                var itemLine = $"{item.Quantity}x {itemName}";
                var price = $"CHF {item.ItemTotal:F2}";
                var spacing = paperWidth == 80 ? 48 : 32;
                var dots = spacing - itemLine.Length - price.Length;

                sb.Append(EXTRA_DARK_ON);
                sb.Append(itemLine);
                sb.Append(new string('.', Math.Max(1, dots)));
                sb.AppendLine(price);
                sb.Append(EXTRA_DARK_OFF);
            }
        }
        else
        {
            _logger.LogWarning("No items found in cashier receipt for order {OrderNumber}", order.OrderNumber);
            sb.AppendLine("(No items in order)");
        }

        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Subtotal, Tax, Discount, Delivery Fee, Tip - EXTRA DARK
        sb.Append(EXTRA_DARK_ON);
        sb.AppendLine($"Subtotal: CHF {order.SubTotal:F2}");

        if (order.Tax > 0)
        {
            sb.AppendLine($"Tax: CHF {order.Tax:F2}");
        }

        if (order.Discount > 0)
        {
            sb.AppendLine($"Discount ({order.DiscountPercentage}%): -CHF {order.Discount:F2}");
        }

        if (order.DeliveryFee > 0)
        {
            sb.AppendLine($"Delivery Fee: CHF {order.DeliveryFee:F2}");
        }

        if (order.Tip > 0)
        {
            sb.AppendLine($"Tip: CHF {order.Tip:F2}");
        }

        sb.Append(EXTRA_DARK_OFF);
        sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));

        // Total - EXTRA DARK, Bold and larger for maximum visibility
        sb.Append(ESC_DOUBLE_ON);
        sb.Append(EXTRA_DARK_ON);
        sb.AppendLine($"TOTAL: CHF {order.Total:F2}");
        sb.Append(EXTRA_DARK_OFF);
        sb.Append(ESC_DOUBLE_OFF);
        sb.AppendLine();

        // Payment information
        if (order.Payments != null && order.Payments.Any())
        {
            sb.Append(EXTRA_DARK_ON);
            sb.AppendLine("PAYMENT:");
            foreach (var payment in order.Payments)
            {
                sb.AppendLine($"{payment.PaymentMethod}: CHF {payment.Amount:F2}");
            }
            sb.Append(EXTRA_DARK_OFF);
            sb.AppendLine();
        }

        // Delivery address for delivery orders
        if (order.Type == "Delivery" && !string.IsNullOrWhiteSpace(order.DeliveryAddress?.FullAddress))
        {
            sb.AppendLine(new string('-', paperWidth == 80 ? 48 : 32));
            sb.Append(EXTRA_DARK_ON);
            sb.AppendLine("DELIVERY TO:");
            sb.AppendLine(order.DeliveryAddress.FullAddress);
            sb.Append(EXTRA_DARK_OFF);
            sb.AppendLine();
        }

        // Footer
        sb.AppendLine(new string('=', paperWidth == 80 ? 48 : 32));
        sb.Append(ESC_ALIGN_CENTER);
        sb.AppendLine("Thank you for your visit!");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();

        // Feed extra lines before cut to prevent text cutoff
        sb.Append(ESC_FEED_LINES);
        sb.Append(ESC_CUT);

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
        try
        {
            // PC857 encoding (ADR-002) matches the codepage selected in the ESC/POS stream — the
            // exact encode both legacy branches performed, hoisted (byte-identical payloads).
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var bytes = Encoding.GetEncoding(857).GetBytes(content);

            // Phase-2b: transport constructed per configured target (a per-printer value), so it
            // is not a DI singleton. See ADR-006 / PrinterTransportResolver.
            IPrinterTransport transport = PrinterTransportResolver.Resolve(printerName);

            if (transport is WindowsSpoolerTransport)
            {
                // The spooler transport's winspool calls are synchronous Win32; keep them off the
                // caller's context exactly as the legacy Task.Run(PrintToWindowsPrinter) did (an
                // in-transport offload was deliberately declined in PR #47).
                await Task.Run(() => transport.SendAsync(bytes, CancellationToken.None));
            }
            else
            {
                await transport.SendAsync(bytes, CancellationToken.None);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to print to {PrinterName}", printerName);
            return false;
        }
    }
}
