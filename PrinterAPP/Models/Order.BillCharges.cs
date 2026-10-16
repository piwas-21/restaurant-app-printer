namespace PrinterAPP.Models;

public partial class Order
{
    public decimal BillChargeAmount { get; set; }
    public decimal BillingCreditAmount { get; set; }
    public decimal? PayableTotal { get; set; }
    public List<BillCharge> BillCharges { get; set; } = [];
}

public sealed class BillCharge
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
}
