namespace PrinterAPP.Models;

/// <summary>Non-secret readiness report for one logical printer target.</summary>
public sealed class PrinterTargetCapability
{
    public DevicePrintTarget Target { get; set; }
    public bool IsSupported { get; set; }
    public bool IsConfigured { get; set; }
    public bool AutoPrintEnabled { get; set; }
    public string? PrinterName { get; set; }
}
