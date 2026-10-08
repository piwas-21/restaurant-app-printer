using System.Text;
using System.Text.Json;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class AmendmentReceiptLinkageTests
{
    [Fact]
    public void LinkedReplacementCancelsPreviousAndDirectsPreparationToOneSeparateTicket()
    {
        var replacement = Guid.NewGuid();
        var update = new PrinterFeedUpdate
        {
            AmendmentId = Guid.NewGuid(),
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Replace,
                    Previous = new OrderItem { ProductName = "Pizza", Quantity = 1 },
                    Current = new OrderItem { ProductName = "Pasta", Quantity = 1 },
                    ReplacementDispatchedOrderId = replacement,
                    ReplacementDispatchedOrderNumber = "NEW-42\u001b@"
                }
            }
        };
        var receipt = UpdateReceiptComposer.Compose(update);
        Assert.Contains("CANCEL PREVIOUS:", receipt);
        Assert.Contains("REPLACEMENT REFERENCE ONLY:", receipt);
        Assert.Contains($"Replacement ticket: NEW-42@ / {replacement:D}", receipt);
        Assert.Contains("Prepare replacement only from its separate ticket.", receipt);
        Assert.DoesNotContain("TO:", receipt);
        Assert.DoesNotContain("NEW-42\u001b@", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinarySupplementTicketRetainsItsSourceLinkageAndControlBytesAreRemoved()
    {
        var amendment = Guid.NewGuid();
        var source = Guid.NewGuid();
        var order = new Order
        {
            AmendmentPrintContext = new(amendment, source, "OLD-1\u001b@\n")
        };
        var builder = new StringBuilder();
        AmendmentReceiptComposer.AppendKitchenIdentity(builder, order, "en");
        var text = builder.ToString();
        Assert.Contains($"Amendment: {amendment:D}", text);
        Assert.Contains($"Source order: OLD-1@ / {source:D}", text);
        Assert.Contains("Prepare only the items on this ticket.", text);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void OrdinaryOrdersHaveNoAmendmentInstructions()
    {
        var builder = new StringBuilder();
        AmendmentReceiptComposer.AppendKitchenIdentity(builder, new Order(), "en");
        Assert.Empty(builder.ToString());
    }

    [Fact]
    public void BackendWireNamesDeserializeBothDirectionsOfTheLink()
    {
        var source = Guid.NewGuid();
        var amendment = Guid.NewGuid();
        var order = JsonSerializer.Deserialize<Order>(
            $$$"""{"amendmentPrintContext":{"amendmentId":"{{{amendment}}}","sourceOrderId":"{{{source}}}","sourceOrderNumber":"OLD-1"}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(source, order!.AmendmentPrintContext!.SourceOrderId);
        var change = JsonSerializer.Deserialize<PrinterFeedChange>(
            $$$"""{"kind":3,"replacementDispatchedOrderId":"{{{source}}}","replacementDispatchedOrderNumber":"NEW-1"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(source, change!.ReplacementDispatchedOrderId);
        Assert.Equal("NEW-1", change.ReplacementDispatchedOrderNumber);
    }

    [Theory]
    [InlineData("en", "Amendment", "Prepare only")]
    [InlineData("de", "Änderung", "Nur die Artikel")]
    [InlineData("fr", "Modification", "Préparer uniquement")]
    [InlineData("it", "Modifica", "Preparare solo")]
    [InlineData("es", "Modificación", "Preparar solo")]
    [InlineData("nl", "Wijziging", "Bereid alleen")]
    [InlineData("tr", "Değişiklik", "Yalnızca")]
    public void KitchenLinkageUsesTheSelectedPrinterLanguage(string language, string title, string preparation)
    {
        var builder = new StringBuilder();
        AmendmentReceiptComposer.AppendKitchenIdentity(builder, new Order
        { AmendmentPrintContext = new(Guid.NewGuid(), Guid.NewGuid(), "ORIGINAL") }, language);
        Assert.Contains(title + ":", builder.ToString(), StringComparison.Ordinal);
        Assert.Contains(preparation, builder.ToString(), StringComparison.Ordinal);
        if (language != "en") Assert.DoesNotContain("Prepare only", builder.ToString(), StringComparison.Ordinal);
    }
}
