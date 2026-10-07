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
    public void Withdrawal_tombstone_redacts_persisted_text_but_preserves_structural_identity_and_amounts()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), text: "customer asked to remove onions") with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.InstructionChange,
                    Previous = new OrderItem
                    {
                        Id = "line-1", ProductName = "Burger", Quantity = 2,
                        UnitPrice = 12.50m, ItemTotal = 25m, SpecialInstructions = "no onions",
                        SideItems = [new OrderItem
                        {
                            Id = "side-1", ProductName = "Fries", Quantity = 2,
                            UnitPrice = 3m, ItemTotal = 6m, SpecialInstructions = "extra crispy",
                        }],
                    },
                    Current = new OrderItem
                    {
                        Id = "line-1", ProductName = "Burger", Quantity = 2,
                        UnitPrice = 12.50m, ItemTotal = 25m, SpecialInstructions = "no onions",
                    },
                },
            },
        };
        Assert.True(store.AddOrGet(update, out _, out _));
        var withdrawal = update with
        {
            Revision = 2,
            IsWithdrawn = true,
            Text = string.Empty,
            Changes = Array.Empty<PrinterFeedChange>(),
            CreatedAt = update.CreatedAt.AddSeconds(1),
        };

        Assert.True(store.AddOrGet(withdrawal, out _, out var shouldDispatch));
        Assert.True(shouldDispatch);
        var original = Assert.Single(store.GetHistory(), record => record.Key == update.Key);
        Assert.Equal(PrintUpdateJobState.Withdrawn, original.State);
        Assert.True(original.Update.IsWithdrawn);
        Assert.Empty(original.Update.Text);
        var item = original.Update.Changes[0].Previous!;
        Assert.Equal("line-1", item.Id);
        Assert.Equal("Burger", item.ProductName);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(12.50m, item.UnitPrice);
        Assert.Equal(25m, item.ItemTotal);
        Assert.Null(item.SpecialInstructions);
        var side = Assert.Single(item.SideItems!);
        Assert.Equal("side-1", side.Id);
        Assert.Equal(3m, side.UnitPrice);
        Assert.Null(side.SpecialInstructions);

        var restarted = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var withdrawalRecord = Assert.Single(restarted.GetHistory(), record => record.Key == withdrawal.Key);
        Assert.True(withdrawalRecord.Update.IsWithdrawn);
        Assert.Empty(withdrawalRecord.Update.Changes);
        Assert.True(restarted.AddOrGet(update, out var replayed, out shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.Equal(withdrawal.Key, replayed.Key);
    }

    [Theory]
    [InlineData(PrintUpdateJobState.Sent)]
    [InlineData(PrintUpdateJobState.Unknown)]
    public void Newer_withdrawal_restamps_only_the_tombstone_and_redacts_stale_original_cache(
        PrintUpdateJobState originalState)
    {
        using var paths = new FakeAppDataPathProvider();
        var original = PrinterUpdateTestData.Update(
            Guid.NewGuid(), createdAt: DateTime.UtcNow.AddMinutes(-1), text: "private preparation note") with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.InstructionChange,
                    Previous = new OrderItem
                    {
                        Id = "line-1", ProductName = "Burger", Quantity = 1,
                        SpecialInstructions = "no onions",
                    },
                    Current = new OrderItem
                    {
                        Id = "line-1", ProductName = "Burger", Quantity = 1,
                        SpecialInstructions = "no onions",
                    },
                },
            },
        };
        var firstWithdrawal = MakeWithdrawal(original, original.CreatedAt.AddSeconds(1));
        var staleOriginal = new PrintUpdateJobRecord
        {
            Update = original with { IsWithdrawn = true },
            State = originalState,
            FirstSeenAt = original.CreatedAt,
            FailureReason = originalState == PrintUpdateJobState.Unknown ? "ambiguous" : null,
            FinalAcknowledgementQueued = true,
        };
        var oldTombstone = new PrintUpdateJobRecord
        {
            Update = firstWithdrawal,
            State = PrintUpdateJobState.Withdrawn,
            FirstSeenAt = firstWithdrawal.CreatedAt,
            FailureReason = "Withdrawn",
            FinalAcknowledgementQueued = true,
        };
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var jobFile = Path.Combine(paths.AppDataDirectory, "print-update-jobs.json");
        File.WriteAllText(jobFile, JsonSerializer.Serialize(new[] { staleOriginal, oldTombstone }, options));

        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(store.TryAdvanceUpdateCursor("cursor-before"));
        Assert.Equal(2, store.GetHistory().Count);
        var refreshedWithdrawal = firstWithdrawal with { CreatedAt = firstWithdrawal.CreatedAt.AddSeconds(1) };

        Assert.True(store.AddOrGet(refreshedWithdrawal, out var refreshed, out var shouldDispatch));

        Assert.False(shouldDispatch);
        Assert.Equal(refreshedWithdrawal.CreatedAt, refreshed.Update.CreatedAt);
        Assert.Equal(PrintUpdateJobState.Withdrawn, refreshed.State);
        Assert.True(refreshed.FinalAcknowledgementQueued);
        Assert.Equal("cursor-before", store.LoadUpdateCursor());

        var savedOriginal = Assert.Single(store.GetHistory(), record => record.Key == original.Key);
        Assert.Equal(originalState, savedOriginal.State);
        Assert.True(savedOriginal.FinalAcknowledgementQueued);
        Assert.True(savedOriginal.Update.IsWithdrawn);
        Assert.Empty(savedOriginal.Update.Text);
        Assert.Null(savedOriginal.Update.Changes[0].Previous!.SpecialInstructions);
        Assert.Null(savedOriginal.Update.Changes[0].Current!.SpecialInstructions);

        var restarted = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var persistedWithdrawal = Assert.Single(restarted.GetHistory(), record => record.Key == refreshedWithdrawal.Key);
        Assert.Equal(refreshedWithdrawal.CreatedAt, persistedWithdrawal.Update.CreatedAt);
        var persistedOriginal = Assert.Single(restarted.GetHistory(), record => record.Key == original.Key);
        Assert.Equal(originalState, persistedOriginal.State);
        Assert.Empty(persistedOriginal.Update.Text);
        Assert.Null(persistedOriginal.Update.Changes[0].Current!.SpecialInstructions);
    }

    [Fact]
    public void Store_rejects_newer_withdrawal_with_changed_identity_or_preparation_content()
    {
        using var paths = new FakeAppDataPathProvider();
        var baseTime = DateTime.UtcNow.AddMinutes(-1);
        var original = PrinterUpdateTestData.Update(Guid.NewGuid(), createdAt: baseTime);
        var withdrawal = MakeWithdrawal(original, baseTime.AddSeconds(1));
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(store.AddOrGet(withdrawal, out _, out _));
        Assert.True(store.TryBegin(withdrawal.Key));
        Assert.True(store.Complete(withdrawal.Key, PrintUpdateJobState.Withdrawn, "Withdrawn"));
        Assert.True(store.MarkFinalAcknowledgementQueued(withdrawal.Key));
        var newer = withdrawal with { CreatedAt = withdrawal.CreatedAt.AddSeconds(1) };

        Assert.False(store.AddOrGet(newer with { OrderId = Guid.NewGuid() }, out _, out var shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.False(store.AddOrGet(newer with { Text = "private text" }, out _, out shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.False(store.AddOrGet(newer with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Add,
                    Current = new OrderItem { Id = "line-1", ProductName = "Burger", Quantity = 1 },
                },
            },
        }, out _, out shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.False(store.AddOrGet(withdrawal with { CreatedAt = withdrawal.CreatedAt.AddTicks(-1) },
            out _, out shouldDispatch));
        Assert.False(shouldDispatch);

        var saved = Assert.Single(store.GetHistory());
        Assert.Equal(withdrawal.CreatedAt, saved.Update.CreatedAt);
        Assert.True(saved.FinalAcknowledgementQueued);
        Assert.Empty(saved.Update.Text);
        Assert.Empty(saved.Update.Changes);
    }

    [Fact]
    public void Store_recovers_interrupted_print_as_unknown_and_deduplicates_it_after_restart()
    {
        using var paths = new FakeAppDataPathProvider();
        var update = PrinterUpdateTestData.Update();
        var first = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);

        Assert.True(first.AddOrGet(update, out _, out var shouldDispatch));
        Assert.True(shouldDispatch);
        Assert.True(first.TryBegin(update.Key));

        // It is impossible to know whether the printer received bytes before the process stopped.
        var restarted = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.Empty(restarted.GetPending());
        var interrupted = Assert.Single(restarted.GetHistory());
        Assert.Equal(PrintUpdateJobState.Unknown, interrupted.State);
        Assert.Contains("Check the printer", interrupted.FailureReason);
        Assert.Single(restarted.GetPendingFinalAcknowledgements());

        var finalRestart = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.True(finalRestart.AddOrGet(update, out var existing, out shouldDispatch));
        Assert.False(shouldDispatch);
        Assert.Equal(PrintUpdateJobState.Unknown, existing.State);
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
    public void Store_recognizes_a_replayed_structured_payload_after_json_deserialization()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var update = PrinterUpdateTestData.Update() with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Add,
                    Current = new OrderItem { Id = "line-1", ProductName = "Burger", Quantity = 2 },
                },
            },
        };
        var jsonOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var replay = JsonSerializer.Deserialize<PrinterFeedUpdate>(
            JsonSerializer.Serialize(update, jsonOptions), jsonOptions)!;

        Assert.NotSame(update.Changes, replay.Changes);
        Assert.True(store.AddOrGet(update, out _, out var shouldDispatch));
        Assert.True(shouldDispatch);
        Assert.True(store.AddOrGet(replay, out _, out shouldDispatch));
        Assert.True(shouldDispatch);
        Assert.Single(store.GetHistory());
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
    public void Store_retries_known_failures_but_holds_unknown_delivery_for_operator_review()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var states = new[]
        {
            PrintUpdateJobState.Failed,
            PrintUpdateJobState.NotConfigured,
        };

        foreach (var state in states)
        {
            var update = PrinterUpdateTestData.Update(Guid.NewGuid());
            Assert.True(store.AddOrGet(update, out _, out _));
            Assert.True(store.TryBegin(update.Key));
            Assert.True(store.Complete(update.Key, state, state.ToString()));
        }

        var unknown = PrinterUpdateTestData.Update(Guid.NewGuid());
        Assert.True(store.AddOrGet(unknown, out _, out _));
        Assert.True(store.TryBegin(unknown.Key));
        Assert.True(store.Complete(unknown.Key, PrintUpdateJobState.Unknown, "unknown"));

        Assert.Equal(states.Length, store.GetPending().Count);
        Assert.All(store.GetPending(), record => Assert.True(record.IsPending));
        Assert.DoesNotContain(store.GetPending(), record => record.Key == unknown.Key);
        Assert.Contains(store.GetPendingFinalAcknowledgements(), record => record.Key == unknown.Key);
    }

    [Fact]
    public void Store_retains_unknown_and_unacknowledged_outcomes_until_stable_acknowledged_history_can_be_pruned()
    {
        using var paths = new FakeAppDataPathProvider();
        var baseline = DateTime.UtcNow.AddDays(-1);
        var records = Enumerable.Range(0, 10_000)
            .Select(index => new PrintUpdateJobRecord
            {
                Update = PrinterUpdateTestData.Update(Guid.NewGuid()),
                State = PrintUpdateJobState.Sent,
                FirstSeenAt = baseline.AddSeconds(index),
                FinalAcknowledgementQueued = true,
            })
            .ToList();
        records[0] = records[0] with
        {
            State = PrintUpdateJobState.Unknown,
            FinalAcknowledgementQueued = false,
        };
        records[1] = records[1] with { FinalAcknowledgementQueued = false };
        var jsonOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var jobPath = Path.Combine(paths.AppDataDirectory, "print-update-jobs.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(records, jsonOptions));

        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var unknownKey = records[0].Key;
        var unacknowledgedSentKey = records[1].Key;
        var firstPrunableKey = records[2].Key;
        Assert.True(store.AddOrGet(PrinterUpdateTestData.Update(Guid.NewGuid()), out _, out _));

        Assert.Contains(store.GetHistory(), record => record.Key == unknownKey && record.State == PrintUpdateJobState.Unknown);
        Assert.Contains(store.GetHistory(), record => record.Key == unacknowledgedSentKey && !record.FinalAcknowledgementQueued);
        Assert.DoesNotContain(store.GetHistory(), record => record.Key == firstPrunableKey);

        Assert.True(store.MarkFinalAcknowledgementQueued(unacknowledgedSentKey));
        Assert.True(store.AddOrGet(PrinterUpdateTestData.Update(Guid.NewGuid()), out _, out _));

        Assert.Contains(store.GetHistory(), record => record.Key == unknownKey && record.State == PrintUpdateJobState.Unknown);
        Assert.DoesNotContain(store.GetHistory(), record => record.Key == unacknowledgedSentKey);
    }

    [Fact]
    public void Store_marks_final_acknowledgement_only_after_outbox_acceptance()
    {
        using var paths = new FakeAppDataPathProvider();
        var store = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        var update = PrinterUpdateTestData.Update();
        Assert.True(store.AddOrGet(update, out _, out _));
        Assert.True(store.TryBegin(update.Key));
        Assert.True(store.Complete(update.Key, PrintUpdateJobState.Sent));

        Assert.Single(store.GetPendingFinalAcknowledgements());
        Assert.True(store.MarkFinalAcknowledgementQueued(update.Key));
        Assert.Empty(store.GetPendingFinalAcknowledgements());

        var restarted = new PrintUpdateJobStore(paths, NullLogger<PrintUpdateJobStore>.Instance);
        Assert.Empty(restarted.GetPendingFinalAcknowledgements());
        Assert.True(restarted.GetHistory().Single().FinalAcknowledgementQueued);
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
    public void Manual_copy_receipt_has_duplicate_warning_and_keeps_only_the_typed_delta()
    {
        var update = PrinterUpdateTestData.Update() with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Add,
                    Current = new OrderItem { Id = "new-line", ProductName = "New bowl", Quantity = 1 },
                },
            },
        };

        var original = UpdateReceiptComposer.Compose(update);
        var copy = UpdateReceiptComposer.Compose(update, isCopy: true);

        Assert.Contains("KITCHEN CHANGE", copy);
        Assert.Contains("COPY - POSSIBLE DUPLICATE", copy);
        Assert.Contains("New bowl", copy);
        Assert.DoesNotContain("COPY - POSSIBLE DUPLICATE", original);
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
    public void Amendment_receipt_renders_only_typed_deltas_and_full_instruction_snapshots()
    {
        var update = PrinterUpdateTestData.Update(
            text: "Cancel Earlier Order Salad; remove the unwanted item\u001b@") with
        {
            ServiceSessionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            AmendmentId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            AccountRevision = 9,
            Target = DevicePrintTarget.FrontKitchen,
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Add,
                    Current = new OrderItem
                    {
                        Id = "line-burger",
                        ProductName = "Burger",
                        Quantity = 2,
                        SpecialInstructions = "No onions",
                        IngredientCustomizations = new List<IngredientCustomization>
                        {
                            new() { IngredientName = "Onion", IsRemoved = true },
                        },
                    },
                },
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Void,
                    Previous = new OrderItem
                    {
                        Id = "line-lemonade",
                        ProductName = "Lemonade",
                        Quantity = 1,
                    },
                },
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Replace,
                    Previous = new OrderItem
                    {
                        Id = "line-old-soup",
                        ProductName = "Old Soup",
                        Quantity = 1,
                    },
                    Current = new OrderItem
                    {
                        Id = "line-new-soup",
                        ProductName = "New Soup",
                        Quantity = 2,
                    },
                },
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.InstructionChange,
                    Previous = new OrderItem
                    {
                        Id = "line-salad",
                        ProductName = "Salad",
                        Quantity = 1,
                        SpecialInstructions = "No dressing",
                        IngredientCustomizations = new List<IngredientCustomization>
                        {
                            new() { IngredientName = "Old Croutons", IsRemoved = true },
                        },
                        SideItems = new List<OrderItem>
                        {
                            new()
                            {
                                Id = "line-old-topping",
                                ProductName = "Old Topping",
                                Quantity = 1,
                                IngredientCustomizations = new List<IngredientCustomization>
                                {
                                    new() { IngredientName = "Old Garlic", IsRemoved = true },
                                },
                            },
                        },
                    },
                    Current = new OrderItem
                    {
                        Id = "line-salad",
                        ProductName = "Salad",
                        Quantity = 1,
                        SpecialInstructions = "Dressing on side",
                        IngredientCustomizations = new List<IngredientCustomization>
                        {
                            new() { IngredientName = "Cheese", IsRemoved = true },
                        },
                        SideItems = new List<OrderItem>
                        {
                            new()
                            {
                                Id = "line-new-topping",
                                ProductName = "New Topping",
                                Quantity = 1,
                                IngredientCustomizations = new List<IngredientCustomization>
                                {
                                    new() { IngredientName = "Fresh Parsley", Quantity = 2, IsAddOn = true },
                                },
                            },
                        },
                    },
                },
            },
        };

        var receipt = UpdateReceiptComposer.Compose(update);

        Assert.Contains("*** KITCHEN CHANGE ***", receipt);
        Assert.Contains("Station: Front kitchen", receipt);
        Assert.Contains("Visit: 44444444", receipt);
        Assert.Contains("Amendment: 55555555", receipt);
        Assert.Contains("Account revision: 9", receipt);
        Assert.Contains("*** ADD ***", receipt);
        Assert.Contains("2x Burger", receipt);
        Assert.Contains("No onions", receipt);
        Assert.Contains("NO Onion", receipt);
        Assert.Contains("*** CANCEL ***", receipt);
        Assert.Contains("1x Lemonade", receipt);
        Assert.Contains("FROM:", receipt);
        Assert.Contains("1x Old Soup", receipt);
        Assert.Contains("TO:", receipt);
        Assert.Contains("2x New Soup", receipt);
        var previousStart = receipt.IndexOf("PREVIOUS INSTRUCTION / ITEM:", StringComparison.Ordinal);
        var currentStart = receipt.IndexOf("CURRENT INSTRUCTION / ITEM:", StringComparison.Ordinal);
        Assert.True(previousStart >= 0 && currentStart > previousStart);
        var previousSection = receipt[previousStart..currentStart];
        var currentSection = receipt[currentStart..];
        Assert.Contains("No dressing", previousSection);
        Assert.Contains("NO Old Croutons", previousSection);
        Assert.Contains("1x Old Topping", previousSection);
        Assert.Contains("NO Old Garlic", previousSection);
        Assert.DoesNotContain("Dressing on side", previousSection);
        Assert.DoesNotContain("NO Cheese", previousSection);
        Assert.Contains("Dressing on side", currentSection);
        Assert.Contains("NO Cheese", currentSection);
        Assert.Contains("1x New Topping", currentSection);
        Assert.Contains("EXTRA Fresh Parsley x2", currentSection);
        Assert.DoesNotContain("No dressing", currentSection);
        Assert.DoesNotContain("NO Old Croutons", currentSection);
        Assert.Contains("Change note:", receipt);
        Assert.Contains("Cancel Earlier Order Salad; remove the unwanted item@", receipt);
        Assert.True(!receipt.Contains("Cancel Earlier Order Salad; remove the unwanted item\u001b@", StringComparison.Ordinal));
        Assert.True(!receipt.Split('\n').Any(line =>
                line.Contains("1x Earlier Order Salad", StringComparison.Ordinal)),
            "An earlier order item must not be rendered as part of the kitchen delta.");
    }

    [Fact]
    public void Amendment_receipt_sanitizes_item_and_instruction_text_before_composition()
    {
        var update = PrinterUpdateTestData.Update(text: string.Empty) with
        {
            Changes = new[]
            {
                new PrinterFeedChange
                {
                    Kind = KitchenChangeKind.Add,
                    Current = new OrderItem
                    {
                        Id = "line-burger",
                        ProductName = "Burger\u001b@",
                        Quantity = 1,
                        SpecialInstructions = "No onions\u001b@",
                    },
                },
            },
        };

        var receipt = UpdateReceiptComposer.Compose(update);
        var lines = receipt.Split('\n');
        var productLine = Assert.Single(lines, line => line.Contains("Burger@", StringComparison.Ordinal));
        var instructionLine = Assert.Single(lines, line => line.Contains("No onions@", StringComparison.Ordinal));

        Assert.Equal("Burger@", UpdateReceiptComposer.SanitizeNote(update.Changes[0].Current!.ProductName));
        Assert.Contains("Burger@", productLine);
        Assert.Contains("No onions@", instructionLine);
        Assert.True(!productLine.Contains("Burger\u001b@", StringComparison.Ordinal),
            string.Join(',', productLine.Select(character => ((int)character).ToString("X2"))));
        Assert.True(!instructionLine.Contains("No onions\u001b@", StringComparison.Ordinal),
            string.Join(',', instructionLine.Select(character => ((int)character).ToString("X2"))));
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
    public void Station_change_jobs_resolve_to_the_matching_station_with_existing_fallbacks()
    {
        var config = new PrinterConfiguration
        {
            CashierPrinterName = "cashier",
            FrontKitchenPrinterName = "front",
            KitchenPrinterName = "legacy-back",
        };

        var front = UpdateJobRouting.Resolve(DevicePrintTarget.FrontKitchen, config);
        var back = UpdateJobRouting.Resolve(DevicePrintTarget.BackKitchen, config);

        Assert.Equal(KitchenDestinationResolutionKind.ConfiguredStation, front.Kind);
        Assert.Equal("front", front.PrinterName);
        Assert.Equal(KitchenDestinationResolutionKind.ConfiguredStation, back.Kind);
        Assert.Equal("legacy-back", back.PrinterName);
        Assert.False(UpdateJobRouting.IsUpdateTarget(DevicePrintTarget.Cashier));
        Assert.True(UpdateJobRouting.AutoPrintEnabled(DevicePrintTarget.FrontKitchen, config));
    }

    [Fact]
    public async Task Update_print_returns_unknown_not_configured_skipped_and_failed_distinctly()
    {
        using var paths = new FakeAppDataPathProvider();
        var update = PrinterUpdateTestData.Update();

        var unknown = new OrderPrintService(new MarketplaceReceiptComposer(),
            new ConfigPrinter(new PrinterConfiguration()), new NoopRequestLogService(),
            NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(
            KitchenPrintStatus.Unknown,
            (await unknown.PrintUpdateAsync(update with { Audience = "Staff" }, AlwaysAuthorizeUpdate)).Status);
        Assert.Equal(
            KitchenPrintStatus.Unknown,
            (await unknown.PrintUpdateAsync(update with
            {
                Changes = new[] { new PrinterFeedChange { Kind = KitchenChangeKind.Add } },
            }, AlwaysAuthorizeUpdate)).Status);

        var notConfigured = new OrderPrintService(new MarketplaceReceiptComposer(),
            new ConfigPrinter(new PrinterConfiguration { KitchenAutoPrint = true }),
            new NoopRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(KitchenPrintStatus.NotConfigured,
            (await notConfigured.PrintUpdateAsync(update, AlwaysAuthorizeUpdate)).Status);

        var skipped = new OrderPrintService(new MarketplaceReceiptComposer(),
            new ConfigPrinter(new PrinterConfiguration { KitchenAutoPrint = false }),
            new NoopRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(KitchenPrintStatus.Skipped,
            (await skipped.PrintUpdateAsync(update, AlwaysAuthorizeUpdate)).Status);

        var unusedPort = ReserveUnusedPort();
        var failed = new OrderPrintService(new MarketplaceReceiptComposer(),
            new ConfigPrinter(new PrinterConfiguration
            {
                KitchenAutoPrint = true,
                DefaultKitchenPrinterName = $"127.0.0.1:{unusedPort}",
            }), new NoopRequestLogService(), NullLogger<OrderPrintService>.Instance, paths);
        Assert.Equal(KitchenPrintStatus.Failed,
            (await failed.PrintUpdateAsync(update, AlwaysAuthorizeUpdate)).Status);
    }

    private static PrinterFeedUpdate MakeWithdrawal(PrinterFeedUpdate original, DateTime createdAt) => original with
    {
        Revision = 2,
        IsWithdrawn = true,
        Text = string.Empty,
        Changes = Array.Empty<PrinterFeedChange>(),
        CreatedAt = createdAt,
    };

    private static int ReserveUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task<PrinterUpdateAuthorizationResult> AlwaysAuthorizeUpdate(CancellationToken _) =>
        Task.FromResult(PrinterUpdateAuthorizationResult.Authorized);

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
