using PrinterAPP.Models;

namespace PrinterAPP.Services;

public sealed record PrinterCorrectionCopyResult(
    bool WasEligible,
    PrintUpdateJobState? OriginalState,
    KitchenPrintOutcome? Outcome);
