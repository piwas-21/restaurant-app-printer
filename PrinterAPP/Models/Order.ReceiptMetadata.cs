namespace PrinterAPP.Models;

/// <summary>
/// Additive fields returned by the order API for receipt identification and readiness details.
/// Missing values from older backend versions keep the printer's legacy receipt behavior safe.
/// </summary>
public partial class Order
{
    /// <summary>Human-facing sequence within <see cref="BusinessDate"/>; zero means an older feed omitted it.</summary>
    public long DailyOrderNumber { get; set; }

    /// <summary>The tenant's operational date, serialized as yyyy-MM-dd.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Effective order preparation estimate, or null when the backend cannot determine one.</summary>
    public int? EffectivePreparationMinutes { get; set; }

    /// <summary>Expected ready instant in UTC; null while staff approval is still pending.</summary>
    public DateTime? ExpectedReadyAt { get; set; }

    /// <summary>Tenant timezone used to render <see cref="ExpectedReadyAt"/> on paper.</summary>
    public string BusinessTimeZoneId { get; set; } = string.Empty;
}
