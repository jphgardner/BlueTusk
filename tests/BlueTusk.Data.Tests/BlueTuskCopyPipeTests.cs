using BlueTusk.Data.Copy;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCopyPipeTests
{
    [Fact]
    public async Task Repeated_pending_reads_preserve_values_and_finish_cleanly()
    {
        await using var pipe = new BlueTuskCopyPipe();
        var sent = new byte[1];
        var received = new byte[1];
        for (var index = 0; index < 128; index++)
        {
            var read = pipe.ReadAsync(received);
            Assert.False(read.IsCompleted);
            sent[0] = (byte)index;
            await pipe.WriteChunkAsync(sent);
            Assert.Equal(1, await read);
            Assert.Equal(sent[0], received[0]);
        }
        pipe.CompleteWriting();
        Assert.Equal(0, await pipe.ReadAsync(received));
    }

    [Fact]
    public async Task Cancelled_pending_reads_do_not_poison_subsequent_reads()
    {
        await using var pipe = new BlueTuskCopyPipe();
        var received = new byte[1];
        for (var index = 0; index < 64; index++)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = pipe.ReadAsync(received, cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await pipe.WriteChunkAsync(new byte[] { 42 });
        pipe.CompleteWriting();
        Assert.Equal(1, await pipe.ReadAsync(received));
        Assert.Equal(42, received[0]);
        Assert.Equal(0, await pipe.ReadAsync(received));
    }

    [Fact]
    public async Task Cancelled_backpressured_write_leaves_queued_chunks_intact()
    {
        await using var pipe = new BlueTuskCopyPipe();
        for (var index = 0; index < 8; index++)
        {
            await pipe.WriteChunkAsync(new byte[] { (byte)index });
        }
        using var cancellation = new CancellationTokenSource();
        var pending = pipe.WriteChunkAsync(new byte[] { 99 }, cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        pipe.CompleteWriting();
        var received = new byte[1];
        for (var index = 0; index < 8; index++)
        {
            Assert.Equal(1, await pipe.ReadAsync(received));
            Assert.Equal((byte)index, received[0]);
        }
        Assert.Equal(0, await pipe.ReadAsync(received));
    }

    [Fact]
    public async Task Coalesced_writes_flush_with_bounded_backpressure()
    {
        await using var pipe = new BlueTuskCopyPipe(coalesceWrites: true);
        var expected = Enumerable.Range(0, 200_001).Select(value => (byte)value).ToArray();
        using var destination = new MemoryStream();
        var read = pipe.CopyToAsync(destination);
        await pipe.WriteChunkAsync(expected);
        await pipe.CompleteWritingAsync();
        await read;
        Assert.Equal(expected, destination.ToArray());
    }
}
