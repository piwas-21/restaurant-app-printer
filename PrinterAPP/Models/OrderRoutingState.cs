namespace PrinterAPP.Models;

/// <summary>
/// Server-owned routing identity for one logical destination of an order. This mirrors the
/// backend <c>OrderRoutingStateDto</c>; nullable route state on <see cref="Order"/> means the
/// server is still on the legacy broadcast contract.
/// </summary>
public sealed class OrderRoutingState
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int Revision { get; set; }
    public DevicePrintTarget Target { get; set; }
    public DevicePrintStatus Status { get; set; }
    /// <summary>Whether the backend requires this route for order completion; informational on the client.</summary>
    public bool IsRequired { get; set; }
    public string? DeviceId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? LastAcknowledgedAt { get; set; }
    public int Version { get; set; }
}
