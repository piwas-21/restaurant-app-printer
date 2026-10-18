using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    [Fact]
    public async Task CashierReceiptOmitsInternalAmendmentIdsAndKitchenDirective()
    {
        using var sink = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var amendment = Guid.NewGuid();
        var source = Guid.NewGuid();
        var order = BundleOrder(Item("Pasta", "FrontKitchen"));
        order.AmendmentPrintContext = new(amendment, source, "OLD-42");
        var (ok, ticket) = await PrintToSinkAsync(order, sink, cancellation.Token);
        Assert.True(ok);
        Assert.Contains("Pasta", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain(amendment.ToString("D"), ticket, StringComparison.Ordinal);
        Assert.DoesNotContain(source.ToString("D"), ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("Prepare only", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("Source order:", ticket, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(58, "en", "Source order: OLD-42", "Prepare only")]
    [InlineData(80, "en", "Source order: OLD-42", "Prepare only")]
    [InlineData(58, "fr", "Commande d'origine: OLD-42", "Préparer uniquement")]
    [InlineData(80, "fr", "Commande d'origine: OLD-42", "Préparer uniquement")]
    public async Task BothKitchenSupplementTicketsKeepOnlyUsefulPreparationInformation(
        int paperWidth, string language, string sourceLabel, string instruction)
    {
        using var cashier = new Sink();
        using var front = new Sink();
        using var back = new Sink();
        using var paths = new TempPathProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var amendment = Guid.NewGuid();
        var source = Guid.NewGuid();
        var order = BundleOrder(Item("Tea", "FrontKitchen"), Item("Pizza", "BackKitchen"));
        order.OrderNumber = "202610180004";
        order.DailyOrderNumber = 4;
        order.AmendmentPrintContext = new(amendment, source, "OLD-42");
        order.Items[1].SpecialInstructions = "No onions";
        var service = new OrderPrintService(new MarketplaceReceiptComposer(),
            new StubPrinterService(new PrinterConfiguration
            {
                CashierPrinterName = cashier.PrinterName,
                FrontKitchenPrinterName = front.PrinterName,
                BackKitchenPrinterName = back.PrinterName,
                FrontKitchenPaperWidth = paperWidth,
                BackKitchenPaperWidth = paperWidth,
                PrintLanguage = language,
            }), new CapturingRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);

        var result = await service.PrintOrderToAllPrintersAsync(order, false, cancellation.Token);
        Assert.True(result.FrontKitchen);
        Assert.True(result.BackKitchen);
        var frontTicket = await front.ReadTicketAsync(cancellation.Token);
        var backTicket = await back.ReadTicketAsync(cancellation.Token);
        foreach (var ticket in new[] { frontTicket, backTicket })
        {
            Assert.Contains(sourceLabel, ticket, StringComparison.Ordinal);
            Assert.Contains(instruction, ticket, StringComparison.Ordinal);
            Assert.Contains("#4", ticket, StringComparison.Ordinal);
            Assert.Contains("202610180004", ticket, StringComparison.Ordinal);
            Assert.Contains("Table 3", ticket, StringComparison.Ordinal);
            Assert.DoesNotContain(amendment.ToString("D"), ticket, StringComparison.Ordinal);
            Assert.DoesNotContain(source.ToString("D"), ticket, StringComparison.Ordinal);
            AssertKitchenTicketHasNoTechnicalMetadata(ticket);
            // Amendment details must reset the tall order-type style before printing.
            var sourceStart = ticket.IndexOf(sourceLabel, StringComparison.Ordinal);
            var precedingNormalSize = ticket.LastIndexOf(EscPosCommands.SizeNormal, sourceStart, StringComparison.Ordinal);
            var precedingTallSize = ticket.LastIndexOf(EscPosCommands.SizeTall, sourceStart, StringComparison.Ordinal);
            Assert.True(precedingNormalSize > precedingTallSize);
        }
        Assert.Contains("FRONT KITCHEN", frontTicket, StringComparison.Ordinal);
        Assert.Contains("1x Tea", frontTicket, StringComparison.Ordinal);
        Assert.DoesNotContain("Pizza", frontTicket, StringComparison.Ordinal);
        Assert.Contains("Back Kitchen", backTicket, StringComparison.Ordinal);
        Assert.Contains("1x Pizza", backTicket, StringComparison.Ordinal);
        Assert.Contains("No onions", backTicket, StringComparison.Ordinal);
        Assert.DoesNotContain("Tea", backTicket, StringComparison.Ordinal);
    }

    private static void AssertKitchenTicketHasNoTechnicalMetadata(string ticket)
    {
        Assert.DoesNotContain("Visit:", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("Amendment:", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("Account revision:", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("Job:", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain(" / revision ", ticket, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", ticket);
        Assert.DoesNotContain("11111111", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("44444444", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("55555555", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("66666666", ticket, StringComparison.Ordinal);
    }
}
