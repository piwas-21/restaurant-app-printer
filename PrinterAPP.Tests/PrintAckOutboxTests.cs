using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class PrintAckOutboxTests : IDisposable
{
    private sealed class TempPaths : IAppDataPathProvider
    {
        public string AppDataDirectory { get; }
        public string LegacyAppDataDirectory => AppDataDirectory;
        public TempPaths(string dir) => AppDataDirectory = dir;
    }

    private readonly string _dir;
    private readonly PrintAckOutbox _outbox;

    public PrintAckOutboxTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _outbox = new PrintAckOutbox(new TempPaths(_dir), NullLogger<PrintAckOutbox>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static PrintAck Ack() => new()
    {
        OrderId = Guid.NewGuid(),
        Target = DevicePrintTarget.Cashier,
        Status = DevicePrintStatus.Printed,
        ReceivedAt = DateTime.UtcNow,
        Copies = 1,
    };

    [Fact]
    public async Task Flush_SendsPending_AndClearsOnSuccess()
    {
        await _outbox.EnqueueAsync(new[] { Ack(), Ack() });

        var sentBatches = 0;
        await _outbox.FlushAsync(batch => { sentBatches++; return Task.FromResult(true); });

        Assert.Equal(1, sentBatches);
        // Second flush has nothing left to send.
        var secondFlushCalls = 0;
        await _outbox.FlushAsync(_ => { secondFlushCalls++; return Task.FromResult(true); });
        Assert.Equal(0, secondFlushCalls);
    }

    [Fact]
    public async Task Flush_TransientFailure_KeepsAcksForNextCycle()
    {
        await _outbox.EnqueueAsync(new[] { Ack() });

        await _outbox.FlushAsync(_ => Task.FromResult(false));   // sender says "retry"

        // Still pending: a second flush is offered the same ack.
        var retried = 0;
        await _outbox.FlushAsync(_ => { retried++; return Task.FromResult(true); });
        Assert.Equal(1, retried);
    }

    [Fact]
    public async Task Flush_ThrowingSender_KeepsAcks()
    {
        await _outbox.EnqueueAsync(new[] { Ack() });

        await _outbox.FlushAsync(_ => throw new HttpRequestException("boom"));

        var retried = 0;
        await _outbox.FlushAsync(_ => { retried++; return Task.FromResult(true); });
        Assert.Equal(1, retried);
    }

    [Fact]
    public async Task Enqueue_PersistsAcrossInstances()
    {
        await _outbox.EnqueueAsync(new[] { Ack() });

        // A fresh outbox over the same directory (simulating a restart) still has the ack.
        var reopened = new PrintAckOutbox(new TempPaths(_dir), NullLogger<PrintAckOutbox>.Instance);
        var flushed = 0;
        await reopened.FlushAsync(batch => { flushed += batch.Count; return Task.FromResult(true); });
        Assert.Equal(1, flushed);
    }

    [Fact]
    public async Task Enqueue_coalesces_only_the_same_routed_job_identity()
    {
        var orderId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var first = new PrintAck
        {
            OrderId = orderId, Target = DevicePrintTarget.FrontKitchen,
            Status = DevicePrintStatus.Queued, ReceivedAt = DateTime.UtcNow,
            JobId = jobId, Revision = 1, JobType = DevicePrintJobType.Order,
        };
        var terminal = new PrintAck
        {
            OrderId = orderId, Target = DevicePrintTarget.FrontKitchen,
            Status = DevicePrintStatus.Printed, ReceivedAt = first.ReceivedAt,
            Copies = 1, JobId = jobId, Revision = 1, JobType = DevicePrintJobType.Order,
        };
        var otherTarget = new PrintAck
        {
            OrderId = orderId, Target = DevicePrintTarget.BackKitchen,
            Status = DevicePrintStatus.Queued, ReceivedAt = first.ReceivedAt,
            JobId = jobId, Revision = 1, JobType = DevicePrintJobType.Order,
        };

        await _outbox.EnqueueAsync(new[] { first, terminal, otherTarget });

        List<PrintAck>? sent = null;
        await _outbox.FlushAsync(batch =>
        {
            sent = batch.ToList();
            return Task.FromResult(true);
        });

        Assert.Equal(2, sent!.Count);
        Assert.Equal(DevicePrintStatus.Printed,
            Assert.Single(sent, ack => ack.Target == DevicePrintTarget.FrontKitchen).Status);
        Assert.Equal(DevicePrintTarget.BackKitchen,
            Assert.Single(sent, ack => ack.Target == DevicePrintTarget.BackKitchen).Target);
    }

    [Fact]
    public async Task Flush_ChunksLargeQueueIntoBackendSizedBatches()
    {
        await _outbox.EnqueueAsync(Enumerable.Range(0, 501).Select(_ => Ack()).ToList());

        var batchSizes = new List<int>();
        await _outbox.FlushAsync(batch => { batchSizes.Add(batch.Count); return Task.FromResult(true); });

        Assert.Equal(2, batchSizes.Count);   // 500 + 1
        Assert.Equal(500, batchSizes[0]);
        Assert.Equal(1, batchSizes[1]);
    }

    [Fact]
    public async Task Flush_CorruptFile_ResetsRatherThanThrow()
    {
        File.WriteAllText(Path.Combine(_dir, "print-ack-outbox.json"), "{ this is not valid json");

        // Must not throw; nothing to send from a reset queue.
        var calls = 0;
        await _outbox.FlushAsync(_ => { calls++; return Task.FromResult(true); });
        Assert.Equal(0, calls);
    }
}
