namespace PrinterAPP.Models;

/// <summary>Mirrors backend CashierPrintJobDto; receipt is supplied only after an atomic claim.</summary>
public sealed record CashierPrintJob(Guid JobId, string Status, bool IsAutomatic,
    DateTime CreatedAt, Order? Receipt);
