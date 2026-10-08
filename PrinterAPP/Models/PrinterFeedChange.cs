namespace PrinterAPP.Models;

/// <summary>Preparation action represented by one immutable kitchen amendment entry.</summary>
public enum KitchenChangeKind
{
    Add = 1,
    Void = 2,
    Replace = 3,
    InstructionChange = 4,
}

/// <summary>Frozen item snapshots for one kitchen delta; never a request to reload the order.</summary>
public sealed record PrinterFeedChange
{
    public KitchenChangeKind Kind { get; init; }
    public OrderItem? Previous { get; init; }
    public OrderItem? Current { get; init; }
    public Guid? ReplacementDispatchedOrderId { get; init; }
    public string? ReplacementDispatchedOrderNumber { get; init; }
}
