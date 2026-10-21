namespace PrinterAPP.Models;

/// <summary>Per-install cashier receipt preferences; variations and ingredient details default off and contact preferences default on.</summary>
public partial class PrinterConfiguration
{
    public const int DefaultCashierPrintJobTimeoutSeconds = 15;
    public const int MinimumCashierPrintJobTimeoutSeconds = 1;
    public int CashierPrintJobTimeoutSeconds { get; set; } = DefaultCashierPrintJobTimeoutSeconds;

    public bool PrintIngredientsOnCashierReceipt { get; set; } = false;
    public bool PrintVariationsOnCashierReceipt { get; set; } = false;
    public bool PrintCustomerPhoneOnCashierReceipt { get; set; } = true;
    public bool PrintCustomerEmailOnCashierReceipt { get; set; } = true;
    public bool PrintPreparationMinutesOnCashierReceipt { get; set; } = true;
    public bool PrintExpectedReadyAtOnCashierReceipt { get; set; } = true;
}
