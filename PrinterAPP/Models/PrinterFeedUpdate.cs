using System.Text.Json.Serialization;

namespace PrinterAPP.Models;

/// <summary>
/// One additive printer-feed update job. The backend currently emits kitchen operational notes as
/// immutable revision-one jobs. The job identity is <see cref="JobId"/>, <see cref="Revision"/> and
/// <see cref="Target"/>; it is never derived from note text or timestamps.
/// </summary>
public sealed record PrinterFeedUpdate
{
    public Guid JobId { get; init; }
    public int Revision { get; init; }
    public DevicePrintJobType JobType { get; init; } = DevicePrintJobType.Update;
    public DevicePrintTarget Target { get; init; } = DevicePrintTarget.General;
    public Guid OrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public int? TableNumber { get; init; }
    /// <summary>Stable configured-table identity when the update feed supplies it.</summary>
    public Guid? TableId { get; init; }
    /// <summary>Display label captured with the order; null on legacy update jobs.</summary>
    public string? TableLabel { get; init; }
    /// <summary>Explicit table visit associated with this amendment, when the order has one.</summary>
    public Guid? ServiceSessionId { get; init; }
    /// <summary>Stable operation identity shared by all station jobs for this amendment.</summary>
    public Guid? AmendmentId { get; init; }
    /// <summary>Visit-account revision after the amendment, when available.</summary>
    public long? AccountRevision { get; init; }
    /// <summary>Frozen preparation deltas; empty on legacy text-only update jobs.</summary>
    public IReadOnlyList<PrinterFeedChange> Changes { get; init; } = Array.Empty<PrinterFeedChange>();
    public string Audience { get; init; } = "Kitchen";
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    /// <summary>Stable natural key used by the local update store and ack outbox.</summary>
    [JsonIgnore]
    public PrintUpdateJobKey Key => new(JobId, Revision, Target);
}

/// <summary>Stable identity for one update at one logical target.</summary>
public readonly record struct PrintUpdateJobKey(Guid JobId, int Revision, DevicePrintTarget Target);
