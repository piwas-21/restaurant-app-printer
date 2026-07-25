using System.Text.Json;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// <see cref="FeedCursorStore"/> is what stops the app reprinting recent orders every time the
/// process restarts — which, now that the Android foreground service restarts itself (START_STICKY,
/// boot), would otherwise put duplicate tickets on the pass. The rules pinned here: a cursor is
/// never trusted to reach further back than the dedup window can cover, and no failure mode may
/// throw, because the caller is the poll loop.
/// </summary>
public sealed class FeedCursorStoreTests : IDisposable
{
    private readonly FakeAppDataPathProvider _paths = new();

    public void Dispose() => _paths.Dispose();

    private FeedCursorStore CreateStore() => new(_paths);

    private string CursorPath => Path.Combine(_paths.AppDataDirectory, "feed-cursor.json");

    [Fact]
    public void Save_then_Load_round_trips_cursor_and_processed_orders()
    {
        var store = CreateStore();
        var savedAt = DateTime.UtcNow.AddMinutes(-2);
        store.Save(new FeedCursor
        {
            LastPollTime = savedAt,
            ProcessedOrders = { ["ORD-1"] = savedAt, ["ORD-2"] = savedAt },
        });

        var loaded = CreateStore().Load();

        Assert.Equal(savedAt, loaded.LastPollTime, TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "ORD-1", "ORD-2" }, loaded.ProcessedOrders.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Load_with_no_file_starts_at_the_default_look_back()
    {
        var loaded = CreateStore().Load();

        Assert.Empty(loaded.ProcessedOrders);
        Assert.Equal(
            DateTime.UtcNow - FeedCursorStore.MaxLookBack, loaded.LastPollTime, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Load_with_a_corrupt_file_falls_back_instead_of_throwing()
    {
        File.WriteAllText(CursorPath, "{ this is not json");

        var loaded = CreateStore().Load();

        Assert.Empty(loaded.ProcessedOrders);
        Assert.Equal(
            DateTime.UtcNow - FeedCursorStore.MaxLookBack, loaded.LastPollTime, TimeSpan.FromSeconds(5));
    }

    // The dedup entries are what stop a re-fetched order printing twice, and they age out on the
    // feed's 1-hour window. A cursor from a long outage must therefore never be honoured verbatim:
    // it would re-fetch a window no surviving dedup entry covers, and reprint a whole night.
    [Fact]
    public void Load_clamps_a_cursor_older_than_the_max_look_back()
    {
        WriteCursor(new FeedCursor { LastPollTime = DateTime.UtcNow.AddHours(-9) });

        var loaded = CreateStore().Load();

        Assert.Equal(
            DateTime.UtcNow - FeedCursorStore.MaxLookBack, loaded.LastPollTime, TimeSpan.FromSeconds(5));
    }

    // A cursor ahead of the clock (device time corrected backwards, timezone-mangled file) would
    // make modifiedSince exclude every real order and silently stall the feed.
    [Fact]
    public void Load_clamps_a_cursor_in_the_future()
    {
        WriteCursor(new FeedCursor { LastPollTime = DateTime.UtcNow.AddHours(3) });

        var loaded = CreateStore().Load();

        Assert.Equal(
            DateTime.UtcNow - FeedCursorStore.MaxLookBack, loaded.LastPollTime, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Load_tolerates_a_missing_processed_orders_object()
    {
        File.WriteAllText(CursorPath, $"{{\"LastPollTime\":\"{DateTime.UtcNow:o}\"}}");

        var loaded = CreateStore().Load();

        Assert.NotNull(loaded.ProcessedOrders);
        Assert.Empty(loaded.ProcessedOrders);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        CreateStore().Save(new FeedCursor { LastPollTime = DateTime.UtcNow });

        Assert.True(File.Exists(CursorPath));
        Assert.Empty(Directory.GetFiles(_paths.AppDataDirectory, "*.tmp"));
    }

    [Fact]
    public void Save_overwrites_a_previous_cursor_rather_than_appending()
    {
        var store = CreateStore();
        store.Save(new FeedCursor { LastPollTime = DateTime.UtcNow.AddMinutes(-10), ProcessedOrders = { ["old"] = DateTime.UtcNow } });
        store.Save(new FeedCursor { LastPollTime = DateTime.UtcNow.AddMinutes(-1), ProcessedOrders = { ["new"] = DateTime.UtcNow } });

        var loaded = CreateStore().Load();

        Assert.Equal(new[] { "new" }, loaded.ProcessedOrders.Keys);
    }

    // The poll loop calls Save on every advance; an unwritable directory must degrade to "cursor not
    // persisted", never take the feed down.
    [Fact]
    public void Save_never_throws_when_the_path_is_unusable()
    {
        // A file where the directory should be makes every write path fail.
        Directory.Delete(_paths.AppDataDirectory, recursive: true);
        File.WriteAllText(_paths.AppDataDirectory, "not a directory");

        var exception = Record.Exception(() => CreateStore().Save(new FeedCursor { LastPollTime = DateTime.UtcNow }));

        Assert.Null(exception);
    }

    private void WriteCursor(FeedCursor cursor)
    {
        Directory.CreateDirectory(_paths.AppDataDirectory);
        File.WriteAllText(CursorPath, JsonSerializer.Serialize(cursor));
    }
}
