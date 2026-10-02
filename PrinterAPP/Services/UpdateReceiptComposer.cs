using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Composes an additive UPDATE ticket. It deliberately accepts only the small feed projection, so a
/// note can still print after the original order has fallen outside the order cursor window.
/// </summary>
public static class UpdateReceiptComposer
{
    private const int MaxNoteCharacters = 4_000;

    /// <summary>Builds one framed, left-aligned UPDATE receipt.</summary>
    public static string Compose(PrinterFeedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Changes is { Count: > 0 })
            return ComposeAmendment(update);

        var builder = new StringBuilder();
        builder.Append(EscPosCommands.Initialize);
        builder.Append(EscPosCommands.CodepageTurkish);
        builder.Append(EscPosCommands.AlignLeft);
        builder.Append(EscPosCommands.BoldOn);
        builder.AppendLine("*** UPDATE ***");
        builder.Append(EscPosCommands.BoldOff);
        builder.AppendLine($"Order: {SanitizeField(update.OrderNumber)}");
        if (TableDisplay.ResolveLabel(update.TableLabel, update.TableNumber) is { } tableLabel)
        {
            builder.AppendLine($"Table: {tableLabel}");
        }

        builder.AppendLine("Note:");
        builder.AppendLine(SanitizeNote(update.Text));
        builder.Append(EscPosCommands.Feed3Lines);
        builder.Append(EscPosCommands.FullCut);
        return builder.ToString();
    }

    private static string ComposeAmendment(PrinterFeedUpdate update)
    {
        var builder = new StringBuilder();
        builder.Append(EscPosCommands.Initialize);
        builder.Append(EscPosCommands.CodepageTurkish);
        builder.Append(EscPosCommands.AlignLeft);
        builder.Append(EscPosCommands.BoldOn);
        builder.AppendLine("*** KITCHEN CHANGE ***");
        builder.Append(EscPosCommands.BoldOff);
        builder.AppendLine($"Order: {SanitizeField(update.OrderNumber)}");
        if (TableDisplay.ResolveLabel(update.TableLabel, update.TableNumber) is { } tableLabel)
            builder.AppendLine($"Table: {tableLabel}");
        AppendIdentity(builder, "Visit", update.ServiceSessionId);
        AppendIdentity(builder, "Amendment", update.AmendmentId);
        if (update.AccountRevision is { } accountRevision)
            builder.AppendLine($"Account revision: {accountRevision}");
        builder.AppendLine($"Job: {FormatId(update.JobId)} / revision {update.Revision}");
        builder.AppendLine($"Station: {StationLabel(update.Target)}");

        foreach (var change in update.Changes ?? Array.Empty<PrinterFeedChange>())
        {
            AppendChange(builder, change);
        }

        builder.Append(EscPosCommands.Feed3Lines);
        builder.Append(EscPosCommands.FullCut);
        return builder.ToString();
    }

    private static void AppendChange(StringBuilder builder, PrinterFeedChange change)
    {
        builder.Append(EscPosCommands.BoldOn);
        builder.AppendLine(change.Kind switch
        {
            KitchenChangeKind.Add => "*** ADD ***",
            KitchenChangeKind.Void => "*** CANCEL ***",
            KitchenChangeKind.Replace => "*** CHANGE ***",
            KitchenChangeKind.InstructionChange => "*** INSTRUCTION CHANGE ***",
            _ => "*** UNKNOWN CHANGE ***",
        });
        builder.Append(EscPosCommands.BoldOff);

        switch (change.Kind)
        {
            case KitchenChangeKind.Add when change.Current is not null:
                AppendSnapshot(builder, change.Current);
                break;
            case KitchenChangeKind.Void when change.Previous is not null:
                AppendSnapshot(builder, change.Previous);
                break;
            case KitchenChangeKind.Replace when change.Previous is not null && change.Current is not null:
                builder.AppendLine("FROM:");
                AppendSnapshot(builder, change.Previous);
                builder.AppendLine("TO:");
                AppendSnapshot(builder, change.Current);
                break;
            case KitchenChangeKind.InstructionChange when change.Previous is not null && change.Current is not null:
                AppendInstructionChange(builder, change.Previous, change.Current);
                break;
        }
    }

    private static void AppendSnapshot(StringBuilder builder, OrderItem item) =>
        ReceiptComposer.AppendKitchenItemLines(builder, SanitizeItem(item), 0, PrintLabelCatalog.English);

    private static void AppendInstructionChange(StringBuilder builder, OrderItem previous, OrderItem current)
    {
        var itemName = SanitizeField(current.ProductName);
        if (!string.IsNullOrWhiteSpace(current.VariationName))
            itemName += $" ({SanitizeField(current.VariationName)})";
        builder.AppendLine($"{current.Quantity}x {itemName}");
        builder.AppendLine($"Previous instruction: {DisplayInstruction(previous.SpecialInstructions)}");
        builder.AppendLine($"Current instruction: {DisplayInstruction(current.SpecialInstructions)}");
    }

    private static string DisplayInstruction(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(none)" : SanitizeNote(text);

    private static void AppendIdentity(StringBuilder builder, string label, Guid? id)
    {
        if (id is { } value)
            builder.AppendLine($"{label}: {FormatId(value)}");
    }

    private static string FormatId(Guid id) => id.ToString("N")[..8].ToUpperInvariant();

    private static string StationLabel(DevicePrintTarget target) => target switch
    {
        DevicePrintTarget.General => "General kitchen",
        DevicePrintTarget.Default => "Default kitchen",
        DevicePrintTarget.FrontKitchen => "Front kitchen",
        DevicePrintTarget.BackKitchen => "Back kitchen",
        _ => "Unknown kitchen",
    };

    private static OrderItem SanitizeItem(OrderItem item) => new()
    {
        Id = item.Id,
        ProductId = item.ProductId,
        ProductVariationId = item.ProductVariationId,
        MenuID = item.MenuID,
        ProductName = SanitizeField(item.ProductName),
        VariationName = SanitizeOptionalField(item.VariationName),
        Quantity = item.Quantity,
        UnitPrice = item.UnitPrice,
        ItemTotal = item.ItemTotal,
        SpecialInstructions = item.SpecialInstructions is null ? null : SanitizeNote(item.SpecialInstructions),
        KitchenType = SanitizeOptionalField(item.KitchenType),
        Kind = item.Kind,
        IngredientCustomizations = item.IngredientCustomizations?.Select(ingredient => new IngredientCustomization
        {
            IngredientId = ingredient.IngredientId,
            IngredientName = SanitizeField(ingredient.IngredientName),
            Quantity = ingredient.Quantity,
            IsRemoved = ingredient.IsRemoved,
            IsAddOn = ingredient.IsAddOn,
        }).ToList(),
        SideItems = item.SideItems?.Select(SanitizeItem).ToList(),
    };

    /// <summary>Compatibility/readability alias for callers that name the rendered artifact.</summary>
    public static string ComposeUpdateReceipt(PrinterFeedUpdate update) => Compose(update);

    /// <summary>
    /// Removes ESC/POS and other control bytes from free text while preserving line breaks. The feed
    /// text is data, never a command stream: a malicious or accidental ESC byte must not reset or
    /// reprogram the physical printer. Length is bounded so a note cannot exhaust the print queue.
    /// </summary>
    public static string SanitizeNote(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, MaxNoteCharacters));
        for (var index = 0; index < text.Length && builder.Length < MaxNoteCharacters; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '\r':
                    // Normalize CRLF and lone CR to one printer line break.
                    if (index + 1 >= text.Length || text[index + 1] != '\n')
                        builder.Append('\n');
                    break;
                case '\n':
                    builder.Append('\n');
                    break;
                case '\t':
                    builder.Append("    ");
                    break;
                default:
                    if (!char.IsControl(character))
                        builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string SanitizeField(string? value) =>
        SanitizeNote(value).Replace('\n', ' ').Trim();

    private static string? SanitizeOptionalField(string? value) =>
        value is null ? null : SanitizeField(value);
}
