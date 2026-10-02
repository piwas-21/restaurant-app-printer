namespace PrinterAPP.Models;

public sealed record OrderAmendmentPrintContext(
    Guid AmendmentId, Guid SourceOrderId, string SourceOrderNumber);
