using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.Transport;

namespace BlueTusk.ConformanceTests;

public sealed class SessionRecoveryConformanceTests
{
    public static TheoryData<string> Operations => new(
        "simple", "simple-async", "nonquery", "extended", "extended-async",
        "scalar", "reset-simple", "reset-simple-async", "reset-extended",
        "reset-extended-async", "reset-scalar", "prepare", "prepare-async",
        "prepared", "prepared-async", "prepared-scalar", "pipeline", "pipeline-async",
        "copy-in", "copy-in-async", "copy-out", "copy-out-async", "copy-both",
        "begin-copy-in", "begin-copy-in-async", "begin-copy-out", "begin-copy-out-async",
        "portal", "portal-async");

    public static TheoryData<string> CancellableOperations => new(
        "simple-async", "nonquery", "extended-async", "scalar", "reset-simple-async",
        "reset-extended-async", "reset-scalar", "prepare-async", "prepared-async",
        "prepared-scalar", "pipeline-async", "copy-in-async", "copy-out-async",
        "copy-both", "begin-copy-in-async", "begin-copy-out-async", "portal-async");

    [Fact]
    public async Task Duplex_ready_boundary_during_CopyDone_discards_the_pending_frontend_frame()
    {
        var transport = new FaultTransport
        {
            Input = Frame('W', [0, 0, 0]),
            HoldRead = true,
            HoldReadAtEnd = true,
            HoldWrite = true,
            UnblockedWrites = 1,
        };
        await using var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var reader = channel.ReadAsync().AsTask();
        await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = channel.CompleteAsync().AsTask();
        try
        {
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.AppendInput(Frame('C', Encoding.UTF8.GetBytes("END\0")).Concat(Frame('Z', [(byte)'I'])).ToArray());
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.WaitAsync(TimeSpan.FromSeconds(3)));
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => completion.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(session.IsReusable);
            Assert.True(transport.IsDisposed);
            Assert.Equal(2, transport.WriteCalls);
        }
        finally
        {
            await ReleaseFixtureAsync(transport, completion);
            await ObserveCleanupAsync(reader);
            await ObserveCleanupAsync(channel.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task Duplex_concurrent_cleanup_waits_for_the_same_actual_transport_close()
    {
        var input = Frame('W', [0, 0, 0]).Concat(Frame('C', Encoding.UTF8.GetBytes("END\0")))
            .Concat(Frame('Z', [(byte)'I'])).ToArray();
        var transport = new FaultTransport { Input = input, HoldWrite = true, UnblockedWrites = 1, HoldDispose = true };
        var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var writer = channel.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reader = channel.ReadAsync().AsTask();
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var abort = channel.AbortAsync().AsTask();
        var disposal = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(reader.IsCompleted);
            Assert.False(abort.IsCompleted);
            Assert.False(disposal.IsCompleted);
            Assert.False(session.IsReusable);
            Assert.False(transport.IsDisposed);
            transport.ResumeDispose.TrySetResult();
            await Task.WhenAll(reader, abort, disposal).WaitAsync(TimeSpan.FromSeconds(3));
            var failure = await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains(nameof(FaultTransport), failure.ObjectName);
            Assert.True(transport.IsDisposed);
            Assert.Equal(2, transport.WriteCalls);
        }
        finally
        {
            transport.ResumeDispose.TrySetResult();
            await ReleaseFixtureAsync(transport, writer);
            await ObserveCleanupAsync(reader);
            await ObserveCleanupAsync(abort);
            await ObserveCleanupAsync(disposal);
            await ObserveCleanupAsync(channel.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dispose")]
    [InlineData("abort")]
    public async Task Duplex_shutdown_is_bounded_while_a_writer_holds_the_channel_lock(string kind)
    {
        var transport = new FaultTransport { Input = Frame('W', [0, 0, 0]), HoldWrite = true, UnblockedWrites = 1 };
        await using var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var writer = channel.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task shutdown = kind switch
        {
            "complete" => channel.CompleteAsync().AsTask(),
            "dispose" => channel.DisposeAsync().AsTask(),
            _ => channel.AbortAsync().AsTask(),
        };
        try
        {
            if (kind == "abort") { await shutdown.WaitAsync(TimeSpan.FromSeconds(8)); }
            else { _ = await Assert.ThrowsAsync<TimeoutException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(8))); }
            Assert.True(shutdown.IsCompleted);
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => writer.WaitAsync(TimeSpan.FromSeconds(2)));
            if (failure is ObjectDisposedException disposed) { Assert.Contains(nameof(FaultTransport), disposed.ObjectName); }
            Assert.Equal(2, transport.WriteCalls);
        }
        finally
        {
            await ReleaseFixtureAsync(transport, writer);
            await ReleaseFixtureAsync(transport, shutdown);
            await ObserveCleanupAsync(channel.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplex_ready_boundary_cannot_reuse_a_session_with_a_pending_frontend_write(bool serverError)
    {
        var end = serverError ? Frame('E', Encoding.UTF8.GetBytes("SERROR\0C40000\0Mstream ended\0\0"))
            : Frame('C', Encoding.UTF8.GetBytes("START_REPLICATION\0"));
        var transport = new FaultTransport
        {
            Input = Frame('W', [0, 0, 0]).Concat(end).Concat(Frame('Z', [(byte)'I'])).ToArray(),
            HoldWrite = true,
            UnblockedWrites = 1,
        };
        await using var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var writer = channel.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reader = channel.ReadAsync().AsTask();
        try
        {
            if (serverError) { _ = await Assert.ThrowsAsync<BlueTuskServerException>(() => reader.WaitAsync(TimeSpan.FromSeconds(8))); }
            else { Assert.Null(await reader.WaitAsync(TimeSpan.FromSeconds(8))); }
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
            _ = await Assert.ThrowsAnyAsync<Exception>(() => writer.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, transport.WriteCalls);
        }
        finally
        {
            await ReleaseFixtureAsync(transport, writer);
            await ObserveCleanupAsync(reader);
            await ObserveCleanupAsync(channel.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task Duplex_session_disposal_closes_transport_without_appending_to_a_pending_write()
    {
        var transport = new FaultTransport { Input = Frame('W', [0, 0, 0]), HoldWrite = true, UnblockedWrites = 1 };
        var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var writer = channel.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = session.DisposeAsync().AsTask();
        try
        {
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(transport.IsDisposed);
            _ = await Assert.ThrowsAnyAsync<Exception>(() => writer.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, transport.WriteCalls);
        }
        finally
        {
            await ReleaseFixtureAsync(transport, writer);
            await ObserveCleanupAsync(disposal);
            await ObserveCleanupAsync(channel.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task Duplex_stale_abort_does_not_invalidate_a_new_channel_owner()
    {
        var input = Frame('W', [0, 0, 0]).Concat(Frame('C', Encoding.UTF8.GetBytes("OLD\0"))).Concat(Frame('Z', [(byte)'I']))
            .Concat(Frame('W', [0, 0, 0])).Concat(Frame('C', Encoding.UTF8.GetBytes("NEW\0"))).Concat(Frame('Z', [(byte)'I'])).ToArray();
        await using var session = CreateSession(new FaultTransport { Input = input }, out _);
        var old = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        Assert.Null(await old.ReadAsync());
        await using var current = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        await old.AbortAsync();
        Assert.True(session.IsOpen);
        Assert.False(session.IsReusable);
        Assert.Equal("NEW", await current.CompleteAsync());
        Assert.True(session.IsReusable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplex_drained_end_preserves_a_healthy_session_without_extra_frontend_frames(bool serverError)
    {
        var end = serverError ? Frame('E', Encoding.UTF8.GetBytes("SERROR\0C40000\0Mstream ended\0\0"))
            : Frame('C', Encoding.UTF8.GetBytes("START_REPLICATION\0"));
        var input = Frame('W', [0, 0, 0]).Concat(end).Concat(Frame('Z', [(byte)'I']))
            .Concat(Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0"))).Concat(Frame('Z', [(byte)'I'])).ToArray();
        var transport = new FaultTransport { Input = input };
        await using var session = CreateSession(transport, out _);
        await using var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        if (serverError) { _ = await Assert.ThrowsAsync<BlueTuskServerException>(() => channel.ReadAsync().AsTask()); }
        else { Assert.Null(await channel.ReadAsync()); }
        Assert.True(session.IsReusable);
        Assert.Equal(1, transport.WriteCalls);
        var next = await session.ExecuteSimpleQueryAsync("UPDATE items SET value = 1");
        Assert.Equal("UPDATE 1", Assert.Single(next.ResultSets).CommandTag);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Duplex_cancelled_read_can_resume_without_releasing_ownership(int framePrefixLength)
    {
        var dataFrame = Frame('d', [1, 2, 3]);
        var transport = new FaultTransport
        {
            Input = Frame('W', [0, 0, 0]).Concat(dataFrame[..framePrefixLength]).ToArray(), HoldRead = true, HoldReadAtEnd = true,
        };
        await using var session = CreateSession(transport, out _);
        await using var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        using var cancellation = new CancellationTokenSource();
        var pending = channel.ReadAsync(cancellation.Token).AsTask();
        await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(session.IsOpen);
        Assert.False(session.IsReusable);
        Assert.False(channel.IsCompleted);
        transport.AppendInput(dataFrame[framePrefixLength..].Concat(Frame('C', Encoding.UTF8.GetBytes("END\0")))
            .Concat(Frame('Z', [(byte)'I'])).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, (await channel.ReadAsync())!.Value.ToArray());
        Assert.Null(await channel.ReadAsync());
        Assert.True(session.IsReusable);
    }

    [Fact]
    public async Task Duplex_complete_and_existing_reader_never_read_the_transport_concurrently()
    {
        var transport = new FaultTransport { Input = Frame('W', [0, 0, 0]), HoldRead = true, HoldReadAtEnd = true };
        await using var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var reader = channel.ReadAsync().AsTask();
        await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = channel.CompleteAsync().AsTask();
        var secondCompletion = channel.CompleteAsync().AsTask();
        try
        {
            await transport.CopyDoneWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(secondCompletion.IsCompleted);
            transport.AppendInput(Frame('C', Encoding.UTF8.GetBytes("END\0")).Concat(Frame('Z', [(byte)'I'])).ToArray());
            await Task.WhenAll(reader, completion, secondCompletion).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("END", await secondCompletion);
            Assert.Equal(1, transport.PeakConcurrentReads);
            Assert.True(session.IsReusable);
            Assert.Equal("END", channel.CommandTag);
        }
        finally
        {
            await ReleaseFixtureAsync(transport, reader);
            await ObserveCleanupAsync(completion);
            await ObserveCleanupAsync(secondCompletion);
            await ObserveCleanupAsync(channel.DisposeAsync().AsTask());
        }
    }

    private static Task ObserveCleanupAsync(Task task) => task.ContinueWith(static completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    [Fact]
    public async Task Disposing_an_aborted_portal_does_not_read_its_old_row_from_the_ready_session()
    {
        byte[] description = [0, 1, (byte)'v', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 23, 0, 4, 255, 255, 255, 255, 0, 1];
        byte[] data = [0, 1, 0, 0, 0, 4, 0, 0, 0, 42];
        var input = Frame('T', description).Concat(Frame('D', data)).Concat(Frame('Z', [(byte)'I']))
            .Concat(Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0"))).Concat(Frame('Z', [(byte)'I'])).ToArray();
        await using var session = CreateSession(new FaultTransport { Input = input }, out _);
        var portal = await session.BeginPortalAsync("SELECT 42", [], fetchSize: 1);
        Assert.NotNull(await portal.ReadAsync());
        await session.AbortPortalAsync(portal);
        Assert.True(session.IsReusable);
        portal.Dispose();
        var next = await session.ExecuteSimpleQueryAsync("UPDATE items SET value = 1");
        Assert.Equal("UPDATE 1", Assert.Single(next.ResultSets).CommandTag);
        Assert.True(session.IsReusable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_portal_fetch_write_is_not_followed_by_cleanup_frames(bool asynchronous)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport
        {
            Input = Frame('n', []).Concat(Frame('s', [])).ToArray(),
            CancelWrite = cancellation,
            UnblockedWrites = 1,
        };
        await using var session = CreateSession(transport, out _);
        await using var portal = await session.BeginPortalAsync("SELECT 1", [], fetchSize: 1);
        if (asynchronous) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => portal.ReadAsync().AsTask()); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => portal.Read()); }
        Assert.False(session.IsOpen);
        Assert.False(session.IsReusable);
        Assert.Equal(2, transport.WriteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Portal_cancellation_after_Sync_uses_a_fence_without_a_second_close_boundary(bool failedTransaction)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fixtureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancelRequest = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(fixtureDeadline.Token);
            var request = new byte[16];
            await peer.GetStream().ReadExactlyAsync(request, fixtureDeadline.Token);
            Assert.Equal(80877102, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4)));
        });
        var transport = new FaultTransport
        {
            Input = Frame('n', []).Concat(Frame('C', Encoding.UTF8.GetBytes("SELECT 1\0"))).ToArray(),
            HoldRead = true,
            HoldReadAtEnd = true,
        };
        await using var session = CreateSession(transport, out _, ((IPEndPoint)listener.LocalEndpoint).Port);
        await using var portal = await session.BeginPortalAsync("SELECT 1", [], fetchSize: 1);
        using var cancellation = new CancellationTokenSource();
        var execution = portal.ReadAsync(cancellation.Token).AsTask();
        try
        {
            await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await cancelRequest.WaitAsync(TimeSpan.FromSeconds(5));
            var tail = Frame('Z', [(byte)(failedTransaction ? 'E' : 'I')]);
            tail = tail.Concat(failedTransaction
                ? Frame('E', Encoding.UTF8.GetBytes("SERROR\0C25P02\0Mtransaction aborted\0\0"))
                : Frame('C', Encoding.UTF8.GetBytes("SELECT 1\0"))).Concat(Frame('Z', [(byte)(failedTransaction ? 'E' : 'I')]))
                .Concat(Frame('C', Encoding.UTF8.GetBytes(failedTransaction ? "ROLLBACK\0" : "UPDATE 1\0")))
                .Concat(Frame('Z', [(byte)'I'])).ToArray();
            transport.AppendInput(tail);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(session.IsReusable);
            Assert.Equal(3, transport.WriteCalls);
            Assert.Equal((byte)'Q', transport.FrontendWrites[2]);
            Assert.Equal(failedTransaction ? BlueTuskTransactionStatus.FailedTransaction : BlueTuskTransactionStatus.Idle, session.TransactionStatus);
            var next = await session.ExecuteSimpleQueryAsync(failedTransaction ? "ROLLBACK" : "UPDATE items SET value = 1");
            Assert.Equal(failedTransaction ? "ROLLBACK" : "UPDATE 1", Assert.Single(next.ResultSets).CommandTag);
            Assert.True(session.IsReusable);
        }
        finally
        {
            await ReleaseFixtureAsync(transport, execution);
            listener.Stop();
            await cancelRequest.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("bulk")]
    [InlineData("complete")]
    [InlineData("duplex")]
    public async Task Pending_COPY_transfer_write_is_cancelled_and_not_followed_by_another_frame(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport
        {
            Input = Frame(kind == "duplex" ? 'W' : 'G', [0, 0, 0]),
            HoldWrite = true,
            UnblockedWrites = 1,
        };
        await using var session = CreateSession(transport, out _);
        var execution = ExecuteCopyWriteAsync(session, kind, cancellation.Token);
        try
        {
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
            Assert.Equal(2, transport.WriteCalls);
        }
        finally { await ReleaseFixtureAsync(transport, execution); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_COPY_output_read_honours_cancellation(bool bulk)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { Input = Frame('H', [0, 0, 0]), HoldRead = true, HoldReadAtEnd = true };
        await using var session = CreateSession(transport, out _);
        var execution = ReadCopyOutputAsync(session, bulk, cancellation.Token);
        try
        {
            await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
        }
        finally { await ReleaseFixtureAsync(transport, execution); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_COPY_done_discards_the_ambiguous_session_without_sending_CopyFail(bool bulk)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { Input = Frame('G', [0, 0, 0]), HoldRead = true, HoldReadAtEnd = true };
        await using var session = CreateSession(transport, out _);
        var execution = CompleteCopyInputAsync(session, bulk, cancellation.Token);
        try
        {
            await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
            Assert.Equal(bulk ? 3 : 2, transport.WriteCalls);
        }
        finally { await ReleaseFixtureAsync(transport, execution); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task COPY_or_portal_disposal_has_a_recovery_deadline(bool portal, bool stalledWrite)
    {
        var transport = new FaultTransport
        {
            Input = Frame(portal ? 'n' : 'G', portal ? [] : [0, 0, 0]),
            HoldWrite = stalledWrite,
            UnblockedWrites = 1,
            HoldRead = !stalledWrite,
            HoldReadAtEnd = true,
        };
        await using var session = CreateSession(transport, out _);
        var execution = DisposeCopyOrPortalAsync(session, portal);
        try
        {
            await (stalledWrite ? transport.WriteStarted.Task : transport.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await execution.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
        }
        finally { await ReleaseFixtureAsync(transport, execution); }
    }

    [Fact]
    public async Task Duplex_COPY_completion_has_a_deadline_and_discards_an_unresponsive_session()
    {
        var transport = new FaultTransport { Input = Frame('W', [0, 0, 0]), HoldRead = true, HoldReadAtEnd = true };
        await using var session = CreateSession(transport, out _);
        var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0");
        var execution = channel.CompleteAsync().AsTask();
        try
        {
            _ = await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(execution.IsCompleted);
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
        }
        finally { await ReleaseFixtureAsync(transport, execution); await channel.DisposeAsync(); }
    }

    [Fact]
    public async Task Synchronous_COPY_data_write_failure_discards_the_session_before_disposal()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { Input = Frame('G', [0, 0, 0]), CancelWrite = cancellation, UnblockedWrites = 1 };
        await using var session = CreateSession(transport, out _);
        using var operation = session.BeginCopyIn("COPY items FROM STDIN");
        Assert.ThrowsAny<OperationCanceledException>(() => operation.Write(new byte[] { 1, 2, 3 }));
        Assert.False(session.IsOpen);
        Assert.False(session.IsReusable);
        Assert.Equal(2, transport.WriteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drained_synchronous_COPY_completion_error_preserves_the_transaction(bool failedTransaction)
    {
        var input = Frame('G', [0, 0, 0]).Concat(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C23514\0Mconstraint failed\0\0")))
            .Concat(Frame('Z', [(byte)(failedTransaction ? 'E' : 'I')])).ToArray();
        await using var session = CreateSession(new FaultTransport { Input = input }, out _);
        using var operation = session.BeginCopyIn("COPY items FROM STDIN");
        var error = Assert.Throws<BlueTuskServerException>(() => operation.Complete());
        Assert.Equal("23514", error.SqlState);
        Assert.True(session.IsOpen);
        Assert.True(session.IsReusable);
        Assert.Equal(failedTransaction ? BlueTuskTransactionStatus.FailedTransaction : BlueTuskTransactionStatus.Idle, session.TransactionStatus);
    }

    private static async Task ExecuteCopyWriteAsync(BlueTuskSession session, string kind, CancellationToken token)
    {
        if (kind == "duplex")
        {
            await using var channel = await session.BeginCopyBothAsync("START_REPLICATION PHYSICAL 0/0", CancellationToken.None);
            await channel.WriteAsync(new byte[] { 1, 2, 3 }, token);
            return;
        }
        if (kind == "bulk")
        {
            using var source = new MemoryStream(new byte[] { 1, 2, 3 });
            _ = await session.CopyInAsync("COPY items FROM STDIN", source, token);
            return;
        }
        var operation = await session.BeginCopyInAsync("COPY items FROM STDIN", null, CancellationToken.None);
        try
        {
            if (kind == "complete") { _ = await operation.CompleteAsync(token); }
            else { await operation.WriteAsync(new byte[] { 1, 2, 3 }, token); }
        }
        finally { await operation.DisposeAsync(); }
    }

    private static async Task ReadCopyOutputAsync(BlueTuskSession session, bool bulk, CancellationToken token)
    {
        if (bulk) { _ = await session.CopyOutAsync("COPY items TO STDOUT", Stream.Null, token); return; }
        var operation = await session.BeginCopyOutAsync("COPY items TO STDOUT", CancellationToken.None);
        try { _ = await operation.ReadAsync(new byte[16], token); }
        finally { await operation.DisposeAsync(); }
    }

    private static async Task CompleteCopyInputAsync(BlueTuskSession session, bool bulk, CancellationToken token)
    {
        if (bulk)
        {
            using var source = new MemoryStream(new byte[] { 1, 2, 3 });
            _ = await session.CopyInAsync("COPY items FROM STDIN", source, token);
            return;
        }
        var operation = await session.BeginCopyInAsync("COPY items FROM STDIN", null, CancellationToken.None);
        try { _ = await operation.CompleteAsync(token); }
        finally { await operation.DisposeAsync(); }
    }

    private static async Task DisposeCopyOrPortalAsync(BlueTuskSession session, bool portal)
    {
        if (portal)
        {
            var operation = await session.BeginPortalAsync("SELECT 1", [], fetchSize: 1);
            await operation.DisposeAsync();
        }
        else
        {
            var operation = await session.BeginCopyInAsync("COPY items FROM STDIN", null);
            await operation.DisposeAsync();
        }
    }

    private static async Task ReleaseFixtureAsync(FaultTransport transport, Task execution)
    {
        transport.ResumeWrite.TrySetResult();
        transport.ResumeRead.TrySetResult();
        await execution.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [Theory]
    [InlineData("simple-async")]
    [InlineData("nonquery")]
    [InlineData("copy-both")]
    public async Task A_queued_operation_rejects_a_session_discarded_by_the_previous_owner(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { HoldWrite = true };
        await using var session = CreateSession(transport, out _);
        var owner = ExecuteAsync(session, "simple-async", cancellation.Token);
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = ExecuteAsync(session, operation);
        Assert.False(queued.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.WaitAsync(TimeSpan.FromSeconds(5)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("The PostgreSQL session is not open.", error.Message);
        Assert.Equal(1, transport.WriteCalls);
        Assert.False(session.IsReusable);
    }

    [Theory]
    [MemberData(nameof(CancellableOperations))]
    public async Task Caller_cancellation_interrupts_a_pending_partial_write(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { HoldWrite = true };
        await using var session = CreateSession(transport, out _);
        var execution = ExecuteAsync(session, operation, cancellation.Token);
        try
        {
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(transport.BytesWritten > 0);
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
        }
        finally
        {
            transport.ResumeWrite.TrySetResult();
            await execution.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    [Theory]
    [InlineData("copy-out-async")]
    [InlineData("begin-copy-out-async")]
    [InlineData("copy-both")]
    public async Task Caller_cancellation_interrupts_a_pending_copy_start_response(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { HoldRead = true };
        await using var session = CreateSession(transport, out _);
        var execution = ExecuteAsync(session, operation, cancellation.Token);
        try
        {
            await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(session.IsOpen);
            Assert.False(session.IsReusable);
        }
        finally
        {
            transport.ResumeRead.TrySetResult();
            await execution.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Disconnected_response_abandons_the_session(string operation)
    {
        var transport = new FaultTransport();
        await using var session = CreateSession(transport, out _);

        await Assert.ThrowsAsync<EndOfStreamException>(() => ExecuteAsync(session, operation));

        Assert.False(session.IsOpen);
        Assert.False(session.IsReusable);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Partial_write_failure_abandons_the_session(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FaultTransport { CancelWrite = cancellation };
        await using var session = CreateSession(transport, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExecuteAsync(session, operation, cancellation.Token));

        Assert.True(transport.BytesWritten > 0);
        Assert.False(session.IsOpen);
        Assert.False(session.IsReusable);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Refused_start_does_not_abandon_another_operation(string operation)
    {
        await using var session = CreateSession(new FaultTransport(), out var protocol);
        protocol.StateMachine.TransitionTo(BlueTuskConnectionState.CopyBoth);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(session, operation));

        Assert.True(session.IsOpen);
        Assert.False(session.IsReusable);
        Assert.Equal(BlueTuskConnectionState.CopyBoth, protocol.StateMachine.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Drained_SQL_error_keeps_the_session_usable(bool asynchronous, bool failedTransaction)
    {
        var input = new List<byte>();
        input.AddRange(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C22012\0Mdivision by zero\0\0")));
        input.AddRange(Frame('Z', [(byte)(failedTransaction ? 'E' : 'I')]));
        input.AddRange(Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")));
        input.AddRange(Frame('Z', [(byte)'I']));
        var transport = new FaultTransport { Input = input.ToArray() };
        await using var session = CreateSession(transport, out _);

        var error = await Assert.ThrowsAsync<BlueTuskServerException>(
            () => ExecuteAsync(session, asynchronous ? "nonquery" : "simple"));

        Assert.Equal("22012", error.SqlState);
        Assert.True(session.IsReusable);
        Assert.Equal(failedTransaction ? BlueTuskTransactionStatus.FailedTransaction : BlueTuskTransactionStatus.Idle,
            session.TransactionStatus);
        // A failed transaction remains available to its owner for an explicit rollback.
        _ = await session.ExecuteSimpleQueryAsync(failedTransaction ? "ROLLBACK" : "UPDATE items SET value = 1");
        Assert.True(session.IsReusable);
        Assert.Equal(BlueTuskTransactionStatus.Idle, session.TransactionStatus);
    }

    private static async Task ExecuteAsync(
        BlueTuskSession session, string operation, CancellationToken token = default)
    {
        const string sql = "UPDATE items SET value = 1";
        switch (operation)
        {
            case "simple": _ = session.ExecuteSimpleQuery(sql); break;
            case "simple-async": _ = await session.ExecuteSimpleQueryAsync(sql, token); break;
            case "nonquery": _ = await session.ExecuteSimpleNonQueryAsync(sql, token); break;
            case "extended": _ = session.ExecuteExtendedQuery(sql, []); break;
            case "extended-async": _ = await session.ExecuteExtendedQueryAsync(sql, [], token); break;
            case "scalar": _ = await session.ExecuteExtendedScalarAsync(sql, [], false, token); break;
            case "reset-simple": _ = session.ExecuteResetAndSimpleQuery(sql, static () => { }); break;
            case "reset-simple-async":
                _ = await session.ExecuteResetAndSimpleQueryAsync(sql, static () => { }, cancellationToken: token);
                break;
            case "reset-extended": _ = session.ExecuteResetAndExtendedQuery(sql, [], false, static () => { }); break;
            case "reset-extended-async":
                _ = await session.ExecuteResetAndExtendedQueryAsync(sql, [], false, static () => { }, cancellationToken: token);
                break;
            case "reset-scalar":
                _ = await session.ExecuteResetAndExtendedScalarAsync(sql, [], false, static () => { }, cancellationToken: token);
                break;
            case "prepare": session.PrepareStatement("test", sql, []); break;
            case "prepare-async": await session.PrepareStatementAsync("test", sql, [], token); break;
            case "prepared": _ = session.ExecutePreparedStatement("test", []); break;
            case "prepared-async": _ = await session.ExecutePreparedStatementAsync("test", [], cancellationToken: token); break;
            case "prepared-scalar": _ = await session.ExecutePreparedScalarAsync("test", [], false, false, token); break;
            case "pipeline": _ = session.ExecutePipeline([new([new(sql, [], false)])]); break;
            case "pipeline-async": _ = await session.ExecutePipelineAsync([new([new(sql, [], false)])], token); break;
            case "copy-in": _ = session.CopyIn(sql, Stream.Null); break;
            case "copy-in-async": _ = await session.CopyInAsync(sql, Stream.Null, token); break;
            case "copy-out": _ = session.CopyOut(sql, Stream.Null); break;
            case "copy-out-async": _ = await session.CopyOutAsync(sql, Stream.Null, token); break;
            case "copy-both": _ = await session.BeginCopyBothAsync(sql, token); break;
            case "begin-copy-in": _ = session.BeginCopyIn(sql); break;
            case "begin-copy-in-async": _ = await session.BeginCopyInAsync(sql, null, token); break;
            case "begin-copy-out": _ = session.BeginCopyOut(sql); break;
            case "begin-copy-out-async": _ = await session.BeginCopyOutAsync(sql, token); break;
            case "portal": _ = session.BeginPortal(sql, []); break;
            case "portal-async": _ = await session.BeginPortalAsync(sql, [], cancellationToken: token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static BlueTuskSession CreateSession(FaultTransport transport, out BlueTuskProtocolConnection protocol, int? cancellationPort = null)
    {
        // Inject deterministic transport failures without timing-dependent socket saturation.
        protocol = new BlueTuskProtocolConnection(transport);
        foreach (var state in new[]
                 {
                     BlueTuskConnectionState.TransportConnected, BlueTuskConnectionState.Startup,
                     BlueTuskConnectionState.Authentication, BlueTuskConnectionState.Initialising,
                     BlueTuskConnectionState.Ready,
                 })
        {
            protocol.StateMachine.TransitionTo(state);
        }
        var constructor = typeof(BlueTuskSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(BlueTuskProtocolConnection), typeof(BlueTuskClientOptions)])!;
        var session = (BlueTuskSession)constructor.Invoke([protocol, new BlueTuskClientOptions
        {
            Host = "127.0.0.1", Port = cancellationPort ?? 5432, Database = "test", Username = "test",
        }]);
        if (cancellationPort.HasValue)
        {
            typeof(BlueTuskSession).GetProperty(nameof(BlueTuskSession.BackendKeyData))!.SetValue(session, new BlueTuskBackendKeyData(123, 456));
        }
        typeof(BlueTuskSession).GetField("_open", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        typeof(BlueTuskSession).GetProperty(nameof(BlueTuskSession.Capabilities))!.SetValue(session,
            BlueTuskServerCapabilities.Detect(new Dictionary<string, string> { ["server_version"] = "18.0" }));
        return session;
    }

    private static byte[] Frame(char identifier, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = (byte)identifier;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame, 5);
        return frame;
    }

    private sealed class FaultTransport : IBlueTuskTransport
    {
        private int _offset;
        private volatile bool _disposed;
        private int _activeReads;
        public bool IsDisposed => _disposed;
        public int PeakConcurrentReads { get; private set; }
        public TaskCompletionSource CopyDoneWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _input = [];
        public byte[] Input { get => _input; init => _input = value; }
        public void AppendInput(byte[] input) { _input = [.. _input, .. input]; ResumeRead.TrySetResult(); }
        public CancellationTokenSource? CancelWrite { get; init; }
        public bool HoldWrite { get; init; }
        public bool HoldRead { get; init; }
        public bool HoldReadAtEnd { get; init; }
        public bool HoldDispose { get; init; }
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int UnblockedWrites { get; init; }
        public int WriteCalls { get; private set; }
        public List<byte> FrontendWrites { get; } = [];
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BytesWritten { get; private set; }
        public EndPoint? RemoteEndPoint => null;
        public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options) { }
        public ValueTask ConnectAsync(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Fragment frames to exercise incomplete message recovery as well as EOF.
            var count = Math.Min(Math.Min(buffer.Length, 3), Input.Length - _offset);
            Input.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeReads);
            PeakConcurrentReads = Math.Max(PeakConcurrentReads, active);
            try
            {
                if (HoldRead && (!HoldReadAtEnd || _offset == Input.Length))
                {
                    ReadStarted.TrySetResult();
                    await ResumeRead.Task.WaitAsync(cancellationToken);
                }
                return Read(buffer.Span);
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }
        public void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCalls++;
            FrontendWrites.Add(buffer[0]);
            WriteCore(buffer);
        }
        private void WriteCore(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer[0] == (byte)'c') { CopyDoneWritten.TrySetResult(); }
            var fail = CancelWrite is not null && WriteCalls > UnblockedWrites;
            BytesWritten += fail ? Math.Max(1, buffer.Length / 2) : buffer.Length;
            if (fail)
            {
                CancelWrite!.Cancel();
                throw new OperationCanceledException(CancelWrite.Token);
            }
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteCalls++;
            FrontendWrites.Add(buffer.Span[0]);
            if (HoldWrite && WriteCalls > UnblockedWrites)
            {
                BytesWritten += Math.Max(1, buffer.Length / 2);
                WriteStarted.TrySetResult();
                await ResumeWrite.Task.WaitAsync(cancellationToken);
                ObjectDisposedException.ThrowIf(_disposed, this);
                return;
            }

            WriteCore(buffer.Span);
        }
        public void Flush() { }
        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Dispose()
        {
            _disposed = true;
            ResumeRead.TrySetResult();
            ResumeWrite.TrySetResult();
        }
        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            if (HoldDispose) { await ResumeDispose.Task; }
            Dispose();
        }
    }
}
