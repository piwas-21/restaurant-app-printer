using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public partial class OrderPrintToSinkTests
{
    [Theory]
    [InlineData(DevicePrintTarget.FrontKitchen)]
    [InlineData(DevicePrintTarget.BackKitchen)]
    [InlineData(DevicePrintTarget.General)]
    [InlineData(DevicePrintTarget.Default)]
    public async Task TypedReplacementCorrectionReachesOnlyItsSinkWithoutEarlierDishes(DevicePrintTarget target)
    {
        using var destination = new Sink();
        using var unrelated = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var paths = new TempPathProvider();
        var config = new PrinterConfiguration
        {
            CashierPrinterName = unrelated.PrinterName,
            FrontKitchenPrinterName = target == DevicePrintTarget.FrontKitchen ? destination.PrinterName : unrelated.PrinterName,
            BackKitchenPrinterName = target == DevicePrintTarget.BackKitchen ? destination.PrinterName : unrelated.PrinterName,
            DefaultKitchenPrinterName = destination.PrinterName,
            KitchenPrinterName = destination.PrinterName,
            FrontKitchenAutoPrint = true,
            BackKitchenAutoPrint = true,
            KitchenAutoPrint = true,
        };
        var service = new OrderPrintService(new MarketplaceReceiptComposer(),
            new StubPrinterService(config), new CapturingRequestLogService(),
            NullLogger<OrderPrintService>.Instance, paths);

        // Positive control: the same socket first receives the original full preparation ticket.
        // Its unaffected Soup must disappear from the later correction, while affected Pizza remains.
        var original = BundleOrder(Item("Pizza", "FrontKitchen"), Item("Soup", "FrontKitchen"));
        Assert.True(await service.PrintOrderAsync(original, PrinterType.Kitchen, true, cancellation.Token));
        var originalTicket = await destination.ReadTicketAsync(cancellation.Token);
        Assert.Equal(1, Occurrences(originalTicket, "Pizza"));
        Assert.Equal(1, Occurrences(originalTicket, "Soup"));

        var parsed = OrderFeedParser.Parse(ReplacementCorrectionEnvelope(target));
        Assert.True(parsed.IsSuccess, parsed.FailureMessage);
        Assert.Empty(parsed.UpdateErrors);
        Assert.Empty(parsed.Orders);
        var update = Assert.Single(parsed.Updates);
        Assert.Equal(target, update.Target);
        Assert.Equal(Guid.Parse("44444444-4444-4444-8444-444444444444"), update.ServiceSessionId);
        Assert.Equal(Guid.Parse("55555555-5555-4555-8555-555555555555"), update.AmendmentId);
        Assert.Equal(KitchenChangeKind.Replace, Assert.Single(update.Changes).Kind);

        var outcome = await service.PrintUpdateAsync(update, cancellation.Token);
        Assert.Equal(KitchenPrintStatus.Sent, outcome.Status);
        var correction = await destination.ReadTicketAsync(cancellation.Token);
        Assert.Contains("\u001b@", correction, StringComparison.Ordinal);
        Assert.Contains("\u001dV\0", correction, StringComparison.Ordinal);
        Assert.Contains("*** KITCHEN CHANGE ***", correction, StringComparison.Ordinal);
        Assert.Contains("CANCEL PREVIOUS:", correction, StringComparison.Ordinal);
        Assert.Contains("REPLACEMENT REFERENCE ONLY:", correction, StringComparison.Ordinal);
        Assert.Contains("Prepare replacement only from its separate ticket.", correction, StringComparison.Ordinal);
        Assert.Contains("Replacement ticket: NEW-42 / 66666666-6666-4666-8666-666666666666", correction, StringComparison.Ordinal);
        Assert.Contains("Visit: 44444444", correction, StringComparison.Ordinal);
        Assert.Contains("Amendment: 55555555", correction, StringComparison.Ordinal);
        Assert.Contains("Account revision: 7", correction, StringComparison.Ordinal);
        Assert.Contains("Table: T-17", correction, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(correction, "Pizza"));
        Assert.Equal(1, Occurrences(correction, "Pasta"));
        Assert.DoesNotContain("Soup", correction, StringComparison.Ordinal);
        Assert.DoesNotContain("TO:", correction, StringComparison.Ordinal);
        Assert.False(unrelated.ReceivedAnything, "The correction contacted a different logical station or the cashier.");
    }

    [Fact]
    public async Task InvalidTypedCorrectionIsHeldWithoutContactingTheSink()
    {
        using var destination = new Sink();
        using var paths = new TempPathProvider();
        var service = new OrderPrintService(new MarketplaceReceiptComposer(),
            new StubPrinterService(new PrinterConfiguration
            {
                FrontKitchenPrinterName = destination.PrinterName,
                FrontKitchenAutoPrint = true,
            }), new CapturingRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        var parsed = OrderFeedParser.Parse(ReplacementCorrectionEnvelope(DevicePrintTarget.FrontKitchen));
        Assert.True(parsed.IsSuccess, parsed.FailureMessage);
        var valid = Assert.Single(parsed.Updates);
        var missingSnapshot = valid with { Changes = [valid.Changes[0] with { Previous = null }] };
        var outcome = await service.PrintUpdateAsync(missingSnapshot);
        Assert.Equal(KitchenPrintStatus.Unknown, outcome.Status);
        Assert.False(destination.ReceivedAnything);
    }

    private static string ReplacementCorrectionEnvelope(DevicePrintTarget target) => $$$"""
        {"success":true,"data":{"orders":[],"updates":[{
          "jobId":"11111111-1111-4111-8111-111111111111","revision":1,"jobType":"Update","target":"{{{target}}}",
          "orderId":"22222222-2222-4222-8222-222222222222","orderNumber":"OLD-42",
          "tableId":"33333333-3333-4333-8333-333333333333","tableLabel":"T-17","tableNumber":null,
          "serviceSessionId":"44444444-4444-4444-8444-444444444444",
          "amendmentId":"55555555-5555-4555-8555-555555555555","accountRevision":7,
          "audience":"Kitchen","text":"Customer changed one dish","createdAt":"2026-10-04T10:00:00Z",
          "changes":[{"kind":"Replace",
            "previous":{"id":"77777777-7777-4777-8777-777777777777","productName":"Pizza","quantity":1,"sideItems":[]},
            "current":{"id":"88888888-8888-4888-8888-888888888888","productName":"Pasta","quantity":1,"sideItems":[]},
            "replacementDispatchedOrderId":"66666666-6666-4666-8666-666666666666","replacementDispatchedOrderNumber":"NEW-42"}]
        }],"nextUpdateCursor":"correction-cursor","hasMoreUpdates":false}}
        """;
}
