namespace PrinterAPP.Services;

/// <summary>How a tenant sends kitchen work to one or more preparation destinations.</summary>
public enum KitchenRoutingMode
{
    /// <summary>Every printable line belongs on one General kitchen ticket.</summary>
    SingleKitchen,

    /// <summary>Front and Back work keep their stations; unassigned work goes to Default.</summary>
    Stations,
}

/// <summary>A logical kitchen ticket, before any physical printer is selected.</summary>
public enum KitchenTicketDestination
{
    General,
    Default,
    FrontKitchen,
    BackKitchen,
}

/// <summary>How an order line names its preparation destination after wire-value normalization.</summary>
public enum KitchenAssignment
{
    Unassigned,
    FrontKitchen,
    BackKitchen,
    Unknown,
}

/// <summary>
/// Explicit tenant routing policy. This stays separate from printer configuration: deciding which
/// work belongs together must not depend on whether a physical printer is currently configured.
/// </summary>
public sealed record KitchenRoutingPolicy(KitchenRoutingMode Mode)
{
    public static KitchenRoutingPolicy SingleKitchen { get; } = new(KitchenRoutingMode.SingleKitchen);

    public static KitchenRoutingPolicy Stations { get; } = new(KitchenRoutingMode.Stations);

    /// <summary>True only for destinations the policy can produce.</summary>
    public bool Includes(KitchenTicketDestination destination) => Mode switch
    {
        KitchenRoutingMode.SingleKitchen => destination == KitchenTicketDestination.General,
        KitchenRoutingMode.Stations => destination is KitchenTicketDestination.Default
            or KitchenTicketDestination.FrontKitchen
            or KitchenTicketDestination.BackKitchen,
        _ => false,
    };

    /// <summary>
    /// Normalizes the backend sentinel and historic absent values. Unknown nonempty values remain
    /// explicit so a later caller can surface configuration guidance instead of guessing a station.
    /// </summary>
    public static KitchenAssignment Classify(string? kitchenType)
    {
        if (string.IsNullOrWhiteSpace(kitchenType)
            || string.Equals(kitchenType, "None", StringComparison.OrdinalIgnoreCase))
        {
            return KitchenAssignment.Unassigned;
        }

        if (string.Equals(kitchenType, "FrontKitchen", StringComparison.OrdinalIgnoreCase))
        {
            return KitchenAssignment.FrontKitchen;
        }

        if (string.Equals(kitchenType, "BackKitchen", StringComparison.OrdinalIgnoreCase))
        {
            return KitchenAssignment.BackKitchen;
        }

        return KitchenAssignment.Unknown;
    }
}
