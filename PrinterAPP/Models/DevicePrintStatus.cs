namespace PrinterAPP.Models;

/// <summary>Outcome of printing one order or update job to one target. Values 1–4 remain the
/// legacy wire contract; new lifecycle values are additive.</summary>
public enum DevicePrintStatus
{
    Received = 1,
    Printed = 2,
    Failed = 3,
    Skipped = 4,
    Queued = 5,
    Sent = 6,
    NotConfigured = 7,
    Unknown = 8,
}
