namespace PrinterAPP.Models;

/// <summary>Per-install cashier receipt preferences; omitted older config properties default on.</summary>
public partial class PrinterConfiguration
{
    public bool PrintCustomerPhoneOnCashierReceipt { get; set; } = true;
    public bool PrintCustomerEmailOnCashierReceipt { get; set; } = true;
    public bool PrintPreparationMinutesOnCashierReceipt { get; set; } = true;
    public bool PrintExpectedReadyAtOnCashierReceipt { get; set; } = true;
}
