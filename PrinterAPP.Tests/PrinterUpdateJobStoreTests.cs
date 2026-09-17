using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class PrinterUpdateJobStoreTests
{
    [Fact]
    public void Store_recovers_processing_work_and_deduplicates_terminal_jobs_after_restart()
    {
        using var paths = new FakeAppDataPathProvider();
        var update = PrinterUpdateTestData.Update();
        var first = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);

        Assert.True(first.AddOrGet(update, out _, out var shouldDispatch));
        Assert.True(shouldDispatch);
        Assert.True(first.TryBegin(update.Key));

        // A new instance sees an interrupted owner as retryable, not as lost work.
        var restarted = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var pending = Assert.Single(restarted.GetPending());
        Assert.Equal(PrintUpdateJobState.Pending, pending.State);
        Assert.True(restarted.TryBegin(update.Key));
        Assert.True(restarted.Complete(update.Key, PrintUpdateJobState.Sent));

        var finalRestart = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(finalRestart.AddOrGet(update, out var existing, out shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.Equal(PrintUpdateJobState.Sent, existing.State);
        Assert.Empty(finalRestart.GetPending());
    }

    [Fact]
    public void Store_rejects_a_changed_payload_for_an_existing_identity()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var update = PrinterUpdateTestData.Update(text: "original");

        Assert.True(store.AddOrGet(update, out _, out _));
        Assert.False(store.AddOrGet(update with { Text = "changed" }, out _, out var shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.Equal("original", Assert.Single(store.GetHistory()).Update.Text);
    }

    [Fact]
    public void Store_rejects_an_empty_cursor_without_replacing_the_durable_cursor()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(store.TryAdvanceUpdateCursor("cursor-1"));

        Assert.False(store.TryAdvanceUpdateCursor("  "));
        Assert.Equal("cursor-1", store.LoadUpdateCursor());
    }

    [Fact]
    public void Store_owns_and_restores_the_update_cursor()
    {
        using var paths = new FakeAppDataPathProvider();
        var first = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);

        Assert.Null(first.LoadUpdateCursor());
        Assert.True(first.TryAdvanceUpdateCursor("created-at/job-a"));

        var restarted = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.Equal("created-at/job-a", restarted.LoadUpdateCursor());
    }

    [Fact]
    public void Store_keeps_failed_not_configured_and_unknown_jobs_pending()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var states = new[]
        {
            PrintUpdateJobState.Failed,
            PrintUpdateJobState.NotConfigured,
            PrintUpdateJobState.Unknown,
        };

        foreach (var state in states)
        {
            var update = PrinterUpdateTestData.Update(Guid.NewGuid());
            Assert.True(store.AddOrGet(update, out _, out _));
            Assert.True(store.TryBegin(update.Key));
            Assert.True(store.Complete(update.Key, state, state.ToString()));
        }

        Assert.Equal(states.Length, store.GetPending().Count);
        Assert.All(store.GetPending(), record => Assert.True(record.IsPending));
    }

    [Fact]
    public void Store_does_not_advance_cursor_when_its_cursor_file_cannot_be_written()
    {
        var root = Path.Combine(Path.GetTempPath(), "printer-update-blocked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blockedDirectory = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedDirectory, "blocked");
        try
        {
            var store = new PrintUpdateJobStore(
                new FixedPaths(blockedDirectory),
                NullLogger<PrintUpdateJobStore>.Instance);

            Assert.False(store.TryAdvanceUpdateCursor("must-not-land"));
            Assert.Null(store.LoadUpdateCursor());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Outbox_coalesces_update_lifecycle_snapshots_and_survives_restart()
    {
        using var paths = new FakeAppDataPathProvider();
        var update = PrinterUpdateTestData.Update();
        var outbox = new PrintAckOutbox(paths, NullLogger<PrintAckOutbox>.Instance);
        var receivedAt = DateTime.UtcNow;

        await outbox.EnqueueAsync(new[] { TelemetryPayloads.UpdateQueuedAck(update, receivedAt) });
        await outbox.EnqueueAsync(new[]
        {
            TelemetryPayloads.UpdateAck(update, KitchenPrintOutcome.Sent, receivedAt),
        });

        var restarted = new PrintAckOutbox(paths, NullLogger<PrintAckOutbox>.Instance);
        List<PrintAck>? batch = null;
        await restarted.FlushAsync(acks =>
        {
            batch = acks.ToList();
            return Task.FromResult(true);
        });

        var ack = Assert.Single(batch!);
        Assert.Equal(DevicePrintStatus.Sent, ack.Status);
        Assert.Equal(update.JobId, ack.JobId);
        Assert.Equal(update.Revision, ack.Revision);
        Assert.Equal(DevicePrintJobType.Update, ack.JobType);
    }

    [Fact]
    public void Update_receipt_removes_control_bytes_but_keeps_line_breaks()
    {
        var sanitized = UpdateReceiptComposer.SanitizeNote("first\u001b@\r\nsecond\tthird\u0007");
        var receipt = UpdateReceiptComposer.Compose(PrinterUpdateTestData.Update(text: "note"));

        Assert.Equal("first@\nsecond    third", sanitized);
        Assert.Contains(EscPosCommands.AlignLeft, receipt);
        Assert.DoesNotContain('\u001b', sanitized);
        Assert.DoesNotContain('\u0007', sanitized);
    }

    [Fact]
    public void Update_receipt_prints_an_alphanumeric_table_label_safely()
    {
        var update = PrinterUpdateTestData.Update() with
        {
            TableNumber = null,
            TableLabel = "T-QA",
        };

        var receipt = UpdateReceiptComposer.Compose(update);

        Assert.Contains("Table: T-QA", receipt);
    }

    [Fact]
    public void Update_receipt_cannot_inject_control_bytes_through_table_label()
    {
        var update = PrinterUpdateTestData.Update() with
        {
            TableNumber = null,
            TableLabel = "T\u001b@\r\nQA",
        };

        var receipt = UpdateReceiptComposer.Compose(update);

        Assert.Contains("Table: T @ QA", receipt);
        Assert.DoesNotContain("T\u001b@\r\nQA", receipt);
    }

    [Fact]
    public void Legacy_order_ack_shape_remains_three_unidentified_receipts()
    {
        var order = new Order { Id = Guid.NewGuid().ToString(), OrderNumber = "LEGACY-1" };
        var config = new PrinterConfiguration
        {
            CashierPrinterName = "cashier",
            FrontKitchenPrinterName = "front",
            BackKitchenPrinterName = "back",
        };

        var acks = TelemetryPayloads.PrintAcks(order, true, true, true, config, DateTime.UtcNow);

        Assert.Equal(3, acks.Count);
        Assert.All(acks, ack =>
        {
            Assert.Null(ack.JobId);
            Assert.Null(ack.Revision);
            Assert.Null(ack.JobType);
        });
    }

    [Theory]
    [InlineData(KitchenPrintStatus.Sent, DevicePrintStatus.Sent, 1)]
    [InlineData(KitchenPrintStatus.Skipped, DevicePrintStatus.Skipped, 0)]
    [InlineData(KitchenPrintStatus.Failed, DevicePrintStatus.Failed, 0)]
    [InlineData(KitchenPrintStatus.NotConfigured, DevicePrintStatus.NotConfigured, 0)]
    [InlineData(KitchenPrintStatus.Unknown, DevicePrintStatus.Unknown, 0)]
    public void Update_ack_preserves_every_explicit_outcome(
        KitchenPrintStatus outcomeStatus, DevicePrintStatus expectedStatus, int expectedCopies)
    {
        var update = PrinterUpdateTestData.Update();
        var ack = TelemetryPayloads.UpdateAck(
            update, new KitchenPrintOutcome(outcomeStatus), DateTime.UtcNow);

        Assert.Equal(expectedStatus, ack.Status);
        Assert.Equal(expectedCopies, ack.Copies);
        Assert.Equal(update.JobId, ack.JobId);
        Assert.Equal(update.Revision, ack.Revision);
        Assert.Equal(DevicePrintJobType.Update, ack.JobType);
    }

    [Fact]
    public void General_and_default_resolve_to_one_owner_without_fanning_out()
    {
        var config = new PrinterConfiguration
        {
            DefaultKitchenPrinterName = "general",
            KitchenPrinterName = "legacy",
            FrontKitchenPrinterName = "front",
            BackKitchenPrinterName = "back",
        };

        var general = UpdateJobRouting.Resolve(DevicePrintTarget.General, config);
        var fallback = UpdateJobRouting.Resolve(DevicePrintTarget.Default, config);

        Assert.Equal(KitchenDestinationResolutionKind.ConfiguredDefault, general.Kind);
        Assert.Equal("general", general.PrinterName);
        Assert.Equal(general, fallback);
    }

    [Fact]
    public async Task Update_print_returns_unknown_not_configured_skipped_and_failed_distinctly()
    {
        using var paths = new FakeAppDataPathProvider();
        var update = PrinterUpdateTestData.Update();

        var unknown = new OrderPrintService(
            new ConfigPrinter(new PrinterConfiguration()), new NoopRequestLogService(),
            NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(
            KitchenPrintStatus.Unknown,
            (await unknown.PrintUpdateAsync(update with { Audience = "Staff" })).Status);

        var notConfigured = new OrderPrintService(
            new ConfigPrinter(new PrinterConfiguration { KitchenAutoPrint = true }),
            new NoopRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(KitchenPrintStatus.NotConfigured, (await notConfigured.PrintUpdateAsync(update)).Status);

        var skipped = new OrderPrintService(
            new ConfigPrinter(new PrinterConfiguration { KitchenAutoPrint = false }),
            new NoopRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(KitchenPrintStatus.Skipped, (await skipped.PrintUpdateAsync(update)).Status);

        var unusedPort = ReserveUnusedPort();
        var failed = new OrderPrintService(
            new ConfigPrinter(new PrinterConfiguration
            {
                KitchenAutoPrint = true,
                DefaultKitchenPrinterName = $"127.0.0.1:{unusedPort}",
            }), new NoopRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(KitchenPrintStatus.Failed, (await failed.PrintUpdateAsync(update)).Status);
    }

    private static int ReserveUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FixedPaths : IAppDataPathProvider
    {
        public FixedPaths(string path) => AppDataDirectory = path;
        public string AppDataDirectory { get; }
        public string LegacyAppDataDirectory => AppDataDirectory;
    }

    private sealed class ConfigPrinter : IPrinterService
    {
        private readonly PrinterConfiguration _configuration;
        public ConfigPrinter(PrinterConfiguration configuration) => _configuration = configuration;
        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) => Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) => Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(_configuration);
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }
}

internal static class PrinterUpdateTestData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static PrinterFeedUpdate Update(
        Guid? jobId = null,
        int revision = 1,
        DevicePrintTarget target = DevicePrintTarget.General,
        DateTime? createdAt = null,
        string text = "Remove onions") => new()
    {
        JobId = jobId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Revision = revision,
        JobType = DevicePrintJobType.Update,
        Target = target,
        OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        OrderNumber = "202609110001",
        TableNumber = 4,
        Audience = "Kitchen",
        Text = text,
        CreatedAt = createdAt ?? DateTime.UtcNow,
    };

    public static string Feed(
        IEnumerable<PrinterFeedUpdate> updates,
        string? nextCursor = null,
        bool hasMore = false) => JsonSerializer.Serialize(
            new
            {
                success = true,
                data = new
                {
                    items = Array.Empty<object>(),
                    updates = updates.ToArray(),
                    nextUpdateCursor = nextCursor,
                    hasMoreUpdates = hasMore,
                },
            }, JsonOptions);

    public static string EmptyFeed(string? nextCursor = null) => Feed(Array.Empty<PrinterFeedUpdate>(), nextCursor);
}
