namespace PrinterAPP.Models;

/// <summary>Logical work represented by a print acknowledgement. The value and name mirror the
/// backend <c>DevicePrintJobType</c> enum. A missing job type remains the legacy order contract.</summary>
public enum DevicePrintJobType
{
    Order = 1,
    Update = 2,
}
