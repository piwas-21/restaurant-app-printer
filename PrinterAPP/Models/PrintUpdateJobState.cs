namespace PrinterAPP.Models;

/// <summary>Durable local lifecycle for an additive printer update job.</summary>
public enum PrintUpdateJobState
{
    /// <summary>The job is stored and eligible for a print attempt.</summary>
    Pending = 1,

    /// <summary>One pipeline owner currently has the job. Reload turns this back into Pending.</summary>
    Processing = 2,

    /// <summary>The bytes left through the printer transport.</summary>
    Sent = 3,

    /// <summary>The job was intentionally skipped (for example, automatic printing is disabled).</summary>
    Skipped = 4,

    /// <summary>The transport failed. It remains eligible for retry.</summary>
    Failed = 5,

    /// <summary>No configured destination exists. It remains eligible for retry.</summary>
    NotConfigured = 6,

    /// <summary>The job could not be safely interpreted. It remains eligible for retry.</summary>
    Unknown = 7,
}
