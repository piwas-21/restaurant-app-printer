using System.Text.Json.Serialization;
namespace PrinterAPP.Models;

public class Order
{
    public string Id { get; set; } = string.Empty;
    public string OrderNumber { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public string Type { get; set; } = string.Empty; // DineIn, TakeAway, Delivery
    public int? TableNumber { get; set; } // Nullable for Takeaway/Delivery orders
    /// <summary>Stable configured-table identity; null is retained for legacy orders.</summary>
    public Guid? TableId { get; set; }
    /// <summary>Display label captured with the order; may be alphanumeric or null on legacy rows.</summary>
    public string? TableLabel { get; set; }
    /// <summary>Explicit table-visit membership; null for legacy and anonymous orders.</summary>
    public Guid? ServiceSessionId { get; set; }

    // Type, with the table appended only when one actually applies (a seated order). Takeaway/
    // Delivery have no table, so we show just the type instead of a dangling "Table" with a blank
    // number. Stable labels take precedence over the legacy numeric compatibility field.
    [JsonIgnore]
    public string TypeDisplay => TableDisplay.FormatOrderType(Type, TableLabel, TableNumber, "Table");
    public decimal SubTotal { get; set; }
    public decimal Tax { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Discount { get; set; }
    public decimal DiscountPercentage { get; set; }

    /// <summary>
    /// Customer-specific discount money, SEPARATE from <see cref="Discount"/> (backend
    /// OrderPricingService.RecalculateTotal: sale = items + fee - Discount - CustomerDiscountAmount).
    /// Without it the printed breakdown cannot reconcile against Total.
    /// </summary>
    public decimal CustomerDiscountAmount { get; set; }
    public decimal Tip { get; set; }

    /// <summary>ISO code the order displays money in (backend OrderDto.Currency). Null = unknown: render bare amounts, never an invented label (POS C18).</summary>
    public string? Currency { get; set; }
    public decimal Total { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal RemainingAmount { get; set; }
    public bool IsFullyPaid { get; set; }
    public bool IsKitchenReleased { get; set; }
    public DateTime? KitchenReleasedAt { get; set; }
    public string? KitchenReleasedBy { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = string.Empty; // Pending, InProgress, Completed, Cancelled
    public string PaymentStatus { get; set; } = string.Empty;
    public List<OrderPermittedAction>? PermittedActions { get; set; }

    public bool IsFocusOrder { get; set; }
    public int? Priority { get; set; }
    public string? FocusReason { get; set; }
    public DateTime? FocusedAt { get; set; }
    public string? FocusedBy { get; set; }
    public string? OrderTypeOverrideBy { get; set; }
    public string? OrderTypeOverrideItems { get; set; }

    public DateTime OrderDate { get; set; }
    public DateTime? EstimatedDeliveryTime { get; set; }
    public DateTime? ActualDeliveryTime { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// The language this order's mails are written in — frozen at creation from the guest's own
    /// request (backend OrderDto.PreferredLanguage). Print-language "auto" resolves against it.
    /// </summary>
    public string? PreferredLanguage { get; set; }

    // Additional Info
    public string? Notes { get; set; }

    /// <summary>Applied voucher code (backend OrderDto.PromoCode) — explains a printed discount.</summary>
    public string? PromoCode { get; set; }
    public bool HasUserLimitDiscount { get; set; }
    public decimal UserLimitAmount { get; set; }
    public string? CancellationReason { get; set; }
    public DeliveryAddress? DeliveryAddress { get; set; }
    public List<OrderItem> Items { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
    public List<OrderStatusHistory> StatusHistory { get; set; } = new();

    /// <summary>
    /// Durable printer routing returned by the forward backend. Null is intentional: older
    /// backend responses omit this additive field and retain the legacy broadcast behavior.
    /// </summary>
    public List<OrderRoutingState>? RoutingStates { get; set; }

    /// <summary>
    /// The same order with a different item list — how a kitchen ticket is built without mutating
    /// the order the history list and the UI are holding. Cloned rather than re-listed field by
    /// field so a field added here can't be silently dropped off a kitchen ticket. Shallow, like
    /// the hand-written copy it replaced: <see cref="Payments"/> and <see cref="StatusHistory"/>
    /// are shared, and neither the receipt composer nor the filter writes to them.
    /// </summary>
    public Order WithItems(List<OrderItem> items)
    {
        var copy = (Order)MemberwiseClone();
        copy.Items = items;
        return copy;
    }
}

public class OrderItem
{
    public string Id { get; set; } = string.Empty;
    public string? ProductId { get; set; }
    public string? ProductVariationId { get; set; }
    public string? MenuID { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? VariationName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemTotal { get; set; }
    public string? SpecialInstructions { get; set; }
    public string? KitchenType { get; set; } // e.g., "FrontKitchen", "BackKitchen", etc.

    /// <summary>
    /// Backend OrderItemDto.Kind, on the wire as the enum NAME
    /// ("SideItem"/"BundleChild"/"CustomizationOption"), null on
    /// top-level and historic rows. Decides what a child's Quantity means: a true side item is
    /// stored PER UNIT of its parent, everything else is already line-absolute (backend
    /// OrderChildRendering.LineQuantity, #318/#305). The printer scales only on this explicit
    /// signal — it has no Product navigation to re-derive the kind from, so an unclassifiable row
    /// prints as stored rather than inventing a multiplier.
    /// </summary>
    public string? Kind { get; set; }

    // Ingredient customizations (added/removed ingredients)
    [JsonPropertyName("ingredientCustomizations")]
    public List<IngredientCustomization>? IngredientCustomizations { get; set; }

    // Side items / additionals (child order items). Backend PR #237 made OrderDto.Items root-only,
    // so this is the ONLY place a bundle component or an add-on side appears — nested, to arbitrary
    // depth. Anything that reasons about "the order's items" has to recurse through it.
    public List<OrderItem>? SideItems { get; set; }

    /// <summary>
    /// Set by <c>KitchenTicketFilter</c> on its own copies: this kitchen does not make this line,
    /// it is on the ticket only to say what the components nested under it belong to. Presentation,
    /// not order data — it is never deserialized, so it does not touch the backend DTO mirror
    /// (§5.3). The renderer cannot derive it from <see cref="KitchenType"/>, because a component
    /// with no kitchen of its own (a drink, an extra sauce) is made wherever its parent is.
    /// </summary>
    [JsonIgnore]
    public bool IsContextOnly { get; set; }

    /// <summary>
    /// The same line with a different child list — see <see cref="Order.WithItems"/> for why this
    /// clones rather than copying field by field. Used by <c>KitchenTicketFilter</c> to route a
    /// subtree to one kitchen without mutating the shared order.
    /// </summary>
    public OrderItem WithSideItems(List<OrderItem>? sideItems)
    {
        var copy = (OrderItem)MemberwiseClone();
        copy.SideItems = sideItems;
        return copy;
    }
}

/// <summary>
/// Ingredient customization for order items (selected/removed ingredients)
/// </summary>
public class IngredientCustomization
{
    public string IngredientId { get; set; } = string.Empty;
    public string IngredientName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsRemoved { get; set; } // true if customer removed this ingredient
    /// <summary>True when this frozen row is a paid optional extra selected by the guest.</summary>
    public bool IsAddOn { get; set; }
}

/// <summary>One explainable action from the backend's permitted order-action projection.</summary>
public class OrderPermittedAction
{
    public string Action { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public string? ReasonCode { get; set; }
    public bool RequiresReason { get; set; }
}

public class Payment
{
    public string Id { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string PaymentMethod { get; set; } = string.Empty; // Cash, Card, etc.
    public decimal Amount { get; set; }
    /// <summary>ISO code of the tender's currency when provided by the feed; null on cash and historical rows.</summary>
    public string? Currency { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? TransactionId { get; set; }
    public string? ReferenceNumber { get; set; }
    public DateTime PaymentDate { get; set; }
    public string? CardLastFourDigits { get; set; }
    public string? CardType { get; set; }
    public string? PaymentGateway { get; set; }
    public string? PaymentNotes { get; set; }
    public bool IsRefunded { get; set; }
    public decimal? RefundedAmount { get; set; }
    public DateTime? RefundDate { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? RefundReason { get; set; }
}

public class OrderStatusHistory
{
    public string Id { get; set; } = string.Empty;
    public string FromStatus { get; set; } = string.Empty;
    public string ToStatus { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
}

public class OrderEvent
{
    public string EventType { get; set; } = string.Empty; // "order-created", "order-updated", etc.
    public Order? Order { get; set; }
    public string? PreviousStatus { get; set; }
    public DateTime Timestamp { get; set; }
}
