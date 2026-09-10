namespace PrinterAPP.Services;

/// <summary>How one kitchen destination's print attempt ended.</summary>
public enum KitchenPrintStatus
{
    /// <summary>The ticket was composed and left through the transport.</summary>
    Sent,

    /// <summary>A print was attempted and the transport reported failure.</summary>
    Failed,

    /// <summary>
    /// Work exists for this destination but no physical printer resolves for it. The kitchen never
    /// saw a ticket, so this is NOT a print and NOT a success — the work stays pending and someone
    /// must configure a destination (issue #113: omitted work used to collapse into "success").
    /// </summary>
    NotConfigured,
}

/// <summary>
/// Typed per-destination print outcome. Replaces the bare <c>bool</c> a destination used to
/// collapse into, which could not tell "printed" from "nothing owed" from "nothing configured".
/// Implicit conversions to and from <see cref="bool"/> keep existing boolean call sites
/// (<c>if (result.FrontKitchen)</c>) compiling with the exact old semantics: true ⇔ sent.
/// <para>Absence of work is NOT a third state: a destination with no ticket owed is trivially
/// satisfied and reports <see cref="KitchenPrintStatus.Sent"/>, exactly as the bool era returned
/// true for empty kitchens. Only a real resolution gap is <see cref="KitchenPrintStatus.NotConfigured"/>.</para>
/// </summary>
public readonly record struct KitchenPrintOutcome(KitchenPrintStatus Status)
{
    public static KitchenPrintOutcome Sent { get; } = new(KitchenPrintStatus.Sent);
    public static KitchenPrintOutcome Failed { get; } = new(KitchenPrintStatus.Failed);
    public static KitchenPrintOutcome NotConfigured { get; } = new(KitchenPrintStatus.NotConfigured);

    /// <summary>True only when the ticket actually went out. NotConfigured and Failed are both false.</summary>
    public bool IsSuccess => Status == KitchenPrintStatus.Sent;

    /// <summary>Source compatibility with the bool tuple elements this replaces.</summary>
    public static implicit operator bool(KitchenPrintOutcome outcome) => outcome.IsSuccess;

    /// <summary>Lets the legacy bool locals flow into the typed result unchanged.</summary>
    public static implicit operator KitchenPrintOutcome(bool success) => success ? Sent : Failed;

    public override string ToString() => Status.ToString();
}
