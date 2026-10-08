using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Connects a normally routed preparation ticket with the source correction.</summary>
public static class AmendmentReceiptComposer
{
    public static void AppendKitchenIdentity(StringBuilder builder, Order order, string? language)
    {
        if (order.AmendmentPrintContext is not { } context) return;
        var labels = AmendmentReceiptLabels.For(language);
        builder.AppendLine($"{labels.Amendment}: {context.AmendmentId:D}");
        builder.AppendLine($"{labels.SourceOrder}: {UpdateReceiptComposer.SanitizeField(context.SourceOrderNumber)} / {context.SourceOrderId:D}");
        builder.AppendLine(labels.PrepareThisTicket);
    }
}
