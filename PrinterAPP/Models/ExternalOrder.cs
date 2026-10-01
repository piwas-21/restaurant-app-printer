namespace PrinterAPP.Models;

/// <summary>Exact additive mirror of backend ExternalOrderDto. No credentials or internal provider IDs.</summary>
public sealed class ExternalOrder
{
    public string Provider { get; set; } = string.Empty;
    public string ExternalDisplayId { get; set; } = string.Empty;
    public string ExternalState { get; set; } = string.Empty;
    public DateTime LastEventAt { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal MerchantTotal { get; set; }
    public decimal? ReportedTax { get; set; }
    public string FulfillmentType { get; set; } = string.Empty;
    public bool IsSandbox { get; set; }
}
