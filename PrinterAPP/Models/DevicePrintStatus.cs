namespace PrinterAPP.Models;

/// <summary>Outcome of printing one order to one target. Names + values mirror the backend
/// <c>DevicePrintStatus</c> exactly. <c>Skipped</c> = no printer configured for the target (vs a
/// genuine <c>Failed</c>) — the distinction the bare success bool couldn't express.</summary>
public enum DevicePrintStatus
{
    Received = 1,
    Printed = 2,
    Failed = 3,
    Skipped = 4
}
