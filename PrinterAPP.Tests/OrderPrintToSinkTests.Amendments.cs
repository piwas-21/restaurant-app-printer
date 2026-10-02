using PrinterAPP.Models;
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

    [Fact]
    public async Task KitchenSupplementTicketCarriesItsSourceAndOnlyNewPreparation()
    {
        using var cashier = new Sink();
        using var front = new Sink();
        using var back = new Sink();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var source = Guid.NewGuid();
        var order = BundleOrder(Item("Pasta", "FrontKitchen"));
        order.AmendmentPrintContext = new(Guid.NewGuid(), source, "OLD-42");
        var result = await PrintToSinksAsync(order, cashier, front, back, cancellation.Token);
        Assert.True(result.FrontKitchen);
        var ticket = await front.ReadTicketAsync(cancellation.Token);
        Assert.Contains($"Source order: OLD-42 / {source:D}", ticket, StringComparison.Ordinal);
        Assert.Contains("Prepare only the items on this ticket.", ticket, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(ticket, "Pasta"));
        Assert.DoesNotContain("Pizza", ticket, StringComparison.Ordinal);
    }
}
