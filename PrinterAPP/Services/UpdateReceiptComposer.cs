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

    private static string SanitizeField(string value) =>
        SanitizeNote(value).Replace('\n', ' ').Trim();
}
