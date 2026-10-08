using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using PrinterAPP.ViewModels;
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

        var outcome = await service.PrintUpdateAsync(update, AlwaysAuthorize, cancellation.Token);
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
        var outcome = await service.PrintUpdateAsync(missingSnapshot, AlwaysAuthorize);
        Assert.Equal(KitchenPrintStatus.Unknown, outcome.Status);
        Assert.False(destination.ReceivedAnything);
    }

    [Fact]
    public async Task StableCorrectionCopyBypassesAutoPrintGateAndLeavesOriginalHistoryUnchanged()
    {
        using var destination = new Sink();
        using var unrelated = new Sink();
        using var paths = new TempPathProvider();
        var config = new PrinterConfiguration
        {
            FrontKitchenPrinterName = destination.PrinterName,
            DefaultKitchenPrinterName = unrelated.PrinterName,
            FrontKitchenAutoPrint = false,
            KitchenAutoPrint = false,
        };
        var jobStore = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var parsed = OrderFeedParser.Parse(ReplacementCorrectionEnvelope(DevicePrintTarget.FrontKitchen));
        Assert.True(parsed.IsSuccess, parsed.FailureMessage);
        var update = Assert.Single(parsed.Updates);
        Assert.True(jobStore.AddOrGet(update, out _, out _));
        Assert.True(jobStore.TryBegin(update.Key));
        Assert.True(jobStore.Complete(update.Key, PrintUpdateJobState.Skipped));
        Assert.True(jobStore.MarkFinalAcknowledgementQueued(update.Key));

        var copyService = new PrinterCorrectionCopyService(
            jobStore,
            new StubPrinterService(config),
            new PrinterOutputService(NullLogger<PrinterOutputService>.Instance),
            new AuthorizedUpdateAuthorizationService(),
            new CapturingRequestLogService(),
            NullLogger<PrinterCorrectionCopyService>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var copy = await copyService.PrintCopyAsync(update.Key, cancellation.Token);
        Assert.True(copy.WasEligible);
        Assert.Equal(KitchenPrintStatus.Sent, copy.Outcome?.Status);
        var ticket = await destination.ReadTicketAsync(cancellation.Token);

        Assert.Contains("COPY - POSSIBLE DUPLICATE", ticket, StringComparison.Ordinal);
        Assert.Contains("CANCEL PREVIOUS:", ticket, StringComparison.Ordinal);
        Assert.Contains("Pasta", ticket, StringComparison.Ordinal);
        Assert.DoesNotContain("Soup", ticket, StringComparison.Ordinal);
        Assert.False(unrelated.ReceivedAnything);
        var unchanged = Assert.Single(jobStore.GetHistory());
        Assert.Equal(PrintUpdateJobState.Skipped, unchanged.State);
        Assert.True(unchanged.FinalAcknowledgementQueued);
    }

    [Fact]
    public async Task Copy_rechecks_authority_immediately_before_output_and_redacts_withdrawn_history()
    {
        using var destination = new Sink();
        using var paths = new TempPathProvider();
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), target: DevicePrintTarget.FrontKitchen,
            text: "customer asked to remove onions");
        var jobStore = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(jobStore.AddOrGet(update, out _, out _));
        Assert.True(jobStore.TryBegin(update.Key));
        Assert.True(jobStore.Complete(update.Key, PrintUpdateJobState.Unknown, "Check the printer."));

        var copyService = new PrinterCorrectionCopyService(
            jobStore,
            new StubPrinterService(new PrinterConfiguration { FrontKitchenPrinterName = destination.PrinterName }),
            new PrinterOutputService(NullLogger<PrinterOutputService>.Instance),
            new AuthorizedUpdateAuthorizationService(PrinterUpdateAuthorizationResult.Withdrawn),
            new CapturingRequestLogService(),
            NullLogger<PrinterCorrectionCopyService>.Instance);

        var result = await copyService.PrintCopyAsync(update.Key);

        Assert.False(result.WasEligible);
        Assert.True(result.WasWithdrawn);
        Assert.Equal(PrintUpdateJobState.Unknown, result.OriginalState);
        Assert.False(destination.ReceivedAnything);
        var saved = Assert.Single(jobStore.GetHistory());
        Assert.Equal(PrintUpdateJobState.Unknown, saved.State);
        Assert.True(saved.Update.IsWithdrawn);
        Assert.Empty(saved.Update.Text);
    }

    [Fact]
    public async Task Automatic_output_is_blocked_when_final_authorization_is_unavailable()
    {
        using var destination = new Sink();
        using var paths = new TempPathProvider();
        var service = new OrderPrintService(new MarketplaceReceiptComposer(),
            new StubPrinterService(new PrinterConfiguration
            {
                FrontKitchenPrinterName = destination.PrinterName,
                FrontKitchenAutoPrint = true,
            }), new CapturingRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        var update = Assert.Single(OrderFeedParser.Parse(
            ReplacementCorrectionEnvelope(DevicePrintTarget.FrontKitchen)).Updates);

        var outcome = await service.PrintUpdateAsync(update,
            _ => Task.FromResult(PrinterUpdateAuthorizationResult.Unavailable));

        Assert.Equal(KitchenPrintStatus.Failed, outcome.Status);
        Assert.False(destination.ReceivedAnything);
    }

    [Theory]
    [InlineData(PrintUpdateJobState.Pending)]
    [InlineData(PrintUpdateJobState.Processing)]
    [InlineData(PrintUpdateJobState.Failed)]
    [InlineData(PrintUpdateJobState.NotConfigured)]
    public async Task Copy_is_blocked_while_the_original_is_active_or_retryable(PrintUpdateJobState state)
    {
        using var destination = new Sink();
        using var paths = new TempPathProvider();
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), target: DevicePrintTarget.FrontKitchen);
        var jobStore = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(jobStore.AddOrGet(update, out _, out _));
        if (state != PrintUpdateJobState.Pending)
        {
            Assert.True(jobStore.TryBegin(update.Key));
            if (state != PrintUpdateJobState.Processing)
                Assert.True(jobStore.Complete(update.Key, state, state.ToString()));
        }

        var copyService = new PrinterCorrectionCopyService(
            jobStore,
            new StubPrinterService(new PrinterConfiguration { FrontKitchenPrinterName = destination.PrinterName }),
            new PrinterOutputService(NullLogger<PrinterOutputService>.Instance),
            new AuthorizedUpdateAuthorizationService(),
            new CapturingRequestLogService(),
            NullLogger<PrinterCorrectionCopyService>.Instance);

        var result = await copyService.PrintCopyAsync(update.Key);

        Assert.False(result.WasEligible);
        Assert.Equal(state, result.OriginalState);
        Assert.Null(result.Outcome);
        Assert.False(destination.ReceivedAnything);
        Assert.Equal(state, Assert.Single(jobStore.GetHistory()).State);
    }

    [Fact]
    public void Copy_confirmation_explains_duplicate_risk_and_staff_check()
    {
        Assert.Contains("may already have reached the kitchen", PrinterCorrectionHistoryViewModel.CopyConfirmationWarning);
        Assert.Contains("Check with staff", PrinterCorrectionHistoryViewModel.CopyConfirmationWarning);
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

    private static Task<PrinterUpdateAuthorizationResult> AlwaysAuthorize(CancellationToken _) =>
        Task.FromResult(PrinterUpdateAuthorizationResult.Authorized);

    private sealed class AuthorizedUpdateAuthorizationService : IPrinterUpdateAuthorizationService
    {
        private readonly PrinterUpdateAuthorizationResult _result;

        public AuthorizedUpdateAuthorizationService(
            PrinterUpdateAuthorizationResult? result = null) =>
            _result = result ?? PrinterUpdateAuthorizationResult.Authorized;

        public Task<PrinterUpdateAuthorizationResult> CheckAsync(
            PrinterFeedUpdate update,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}
