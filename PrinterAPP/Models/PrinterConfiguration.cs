
namespace PrinterAPP.Models;

public class PrinterConfiguration
{
    public const int DefaultUpdateRetryIntervalSeconds = 5;
    public const int MinimumUpdateRetryIntervalSeconds = 1;

    public string ApiBaseUrl { get; set; } = "https://www.rumirestaurant.ch";
    public string ApiKey { get; set; } = "";  // X-Api-Key header value for printer-feed authentication

    // Front Kitchen Printer Settings (for drinks, desserts, etc.)
    public string FrontKitchenPrinterName { get; set; } = "";
    public bool FrontKitchenAutoPrint { get; set; } = true;
    public int FrontKitchenPaperWidth { get; set; } = 80;

    // Back Kitchen Printer Settings (for main dishes, grills, etc.)
    public string BackKitchenPrinterName { get; set; } = "";
    public bool BackKitchenAutoPrint { get; set; } = true;
    public int BackKitchenPaperWidth { get; set; } = 80;

    // Legacy Kitchen Printer (fallback if specific kitchens not configured)
    public string KitchenPrinterName { get; set; } = "";
    public bool KitchenAutoPrint { get; set; } = true;
    public int KitchenPrintCopies { get; set; } = 1;
    public int KitchenPaperWidth { get; set; } = 80;

    // Retry cadence for durable update jobs that remain Failed, NotConfigured or Unknown.
    public int UpdateRetryIntervalSeconds { get; set; } = DefaultUpdateRetryIntervalSeconds;

    // Kitchen routing policy (issue #113): Stations (default) = the shipped Front/Back split,
    // byte-for-byte, plus unassigned work on a resolved Default ticket; SingleKitchen = ONE
    // General ticket. Logic: KitchenRoutingPolicy/KitchenTicketFilter/KitchenDestinationResolver.
    public PrinterAPP.Services.KitchenRoutingMode KitchenRoutingMode { get; set; } = PrinterAPP.Services.KitchenRoutingMode.Stations;
    // Explicit General/Default kitchen printer (KitchenDestinationResolver resolves it first).
    public string DefaultKitchenPrinterName { get; set; } = "";

    // Cashier Printer Settings
    public string CashierPrinterName { get; set; } = "";
    public bool CashierAutoPrint { get; set; } = true;
    public int CashierPrintCopies { get; set; } = 1;
    public int CashierPaperWidth { get; set; } = 80;

    // Time-based Auto-Print Restrictions
    public bool EnableTimeRestriction { get; set; } = false;
    public TimeSpan RestrictStartTime { get; set; } = new TimeSpan(12, 0, 0); // 12:00
    public TimeSpan RestrictEndTime { get; set; } = new TimeSpan(13, 0, 0); // 13:00

    // Legacy properties for backward compatibility
    [Obsolete("Use KitchenPrinterName instead")]
    public string PrinterName { get; set; } = "";
    [Obsolete("Use KitchenAutoPrint instead")]
    public bool AutoPrint { get; set; } = true;
    [Obsolete("Use KitchenPrintCopies instead")]
    public int PrintCopies { get; set; } = 1;
    [Obsolete("Use KitchenPaperWidth instead")]
    public int PaperWidth { get; set; } = 80;

    // Print language for composed tickets: a fixed label language code (en, de, fr, it, es, nl, tr)
    // or "auto" = follow the order's PreferredLanguage with an English fallback. Item and ingredient
    // NAMES print in the language this setting asks the feed for (the poll sends it; the backend
    // translates from the catalog's descriptions and falls back to the frozen checkout names —
    // see PrinterAPP.Services.PrintLanguagePolicy).
    public string PrintLanguage { get; set; } = PrinterAPP.Services.PrintLanguagePolicy.English;

    // Restaurant Information
    public string RestaurantName { get; set; } = "Rumi Restaurant";
    public string KitchenLocation { get; set; } = "Main Kitchen";

    // Service Status
    public bool IsServiceRunning { get; set; } = false;

    // Fleet observability: the control-plane tenant this device belongs to, seeded at provisioning,
    // plus a human-readable device label shown in the admin panel. Used for telemetry and Sentry
    // tagging; never a secret. See docs/plans/PRINTER-APP-FLEET-OBSERVABILITY-PLAN.md.
    public string TenantSlug { get; set; } = "";
    public string DeviceLabel { get; set; } = "";
}
