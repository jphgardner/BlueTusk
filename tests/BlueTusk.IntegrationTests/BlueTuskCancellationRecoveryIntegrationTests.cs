using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskCancellationRecoveryIntegrationTests
{
    [Theory]
    [InlineData("query")]
    [InlineData("non-query")]
    [InlineData("scalar")]
    [InlineData("pipeline")]
    [InlineData("binary-import")]
    [InlineData("binary-export")]
    [InlineData("copy-in")]
    [InlineData("copy-out")]
    [InlineData("binary-transfer")]
    public async Task Stalled_frontend_delivery_has_bounded_cancellation_and_replaces_the_session(string kind)
    {
        var configured = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }
        var settings = new BlueTuskConnectionStringBuilder(configured);
        if (settings.SslMode != BlueTuskSslMode.Disable ||
            !(settings.Host == "localhost" || (IPAddress.TryParse(settings.Host, out var address) && IPAddress.IsLoopback(address))))
        {
            throw SkipException.ForSkip("The stalled-delivery fixture requires a loopback plaintext PostgreSQL test endpoint.");
        }
        await using var proxy = new StalledDeliveryProxy(settings.Host, settings.Port);
        settings.Host = "127.0.0.1";
        settings.Port = proxy.Port;
        settings.MaximumPoolSize = 1;
        await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);
        await using var held = await dataSource.OpenConnectionAsync();
        int backend;
        await using (var identify = new BlueTuskCommand("SELECT pg_backend_pid()::int4", held))
        {
            backend = await identify.ExecuteScalarAsync<int>();
        }
        using var cancellation = new CancellationTokenSource();
        if (kind is "binary-import" or "copy-in" or "binary-transfer")
        {
            await using var setup = new BlueTuskCommand("CREATE TEMP TABLE bluetusk_stalled_import (value " +
                (kind == "binary-transfer" ? "text" : "int4") + ")", held);
            _ = await setup.ExecuteNonQueryAsync();
        }
        proxy.Arm();
        var operation = ExecuteBlockedAsync(held, kind, cancellation.Token);
        try
        {
            await proxy.PartialWrite.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            proxy.Resume();
        }
        Assert.True(proxy.DeclaredBytes > 1024 * 1024);
        Assert.InRange(proxy.ForwardedBytes, 1, proxy.DeclaredBytes - 1);
        if (kind == "binary-transfer") { Assert.Equal((byte)'d', proxy.BlockedIdentifier); }
        await held.CloseAsync();
        for (var index = 0; index < 3; index++)
        {
            await using var next = dataSource.CreateCommand("SELECT pg_backend_pid()::int4");
            Assert.NotEqual(backend, await next.ExecuteScalarAsync<int>());
        }
        Assert.Equal(2, dataSource.GetPoolStatistics().Opened);
        Assert.Equal(1, dataSource.GetPoolStatistics().Discarded);
        Assert.Equal(1, dataSource.GetPoolStatistics().Idle);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Copy_output_abort_preserves_the_session_and_owner_transaction(bool asynchronous, bool insideTransaction)
    {
        var configured = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configured)) { throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured."); }
        var settings = new BlueTuskConnectionStringBuilder(configured) { MaximumPoolSize = 1 };
        await using var source = BlueTuskDataSource.Create(settings.ConnectionString);
        await using (var connection = await source.OpenConnectionAsync())
        {
            await using var identify = new BlueTuskCommand("SELECT pg_backend_pid()::int4", connection);
            var backend = await identify.ExecuteScalarAsync<int>();
            await using (var transaction = insideTransaction ? await connection.BeginTransactionAsync() : null)
            {
                const string sql = "COPY (SELECT CASE WHEN value=1 THEN repeat('x',131072) ELSE pg_sleep(10)::text END " +
                    "FROM generate_series(1,2) AS value) TO STDOUT WITH (FORMAT BINARY)";
                if (asynchronous)
                {
                    await using var exporter = await connection.BeginBinaryExportAsync(sql);
                    Assert.Equal(1, await exporter.StartRowAsync());
                    var first = await exporter.ReadAsync<string>();
                    Assert.NotNull(first);
                    Assert.Equal(131072, first.Length);
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.StartRowAsync(cancellation.Token).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
                }
                else
                {
                    using var cancellation = new CancellationTokenSource();
                    using var destination = new CancelledDestination(cancellation);
                    Assert.ThrowsAny<OperationCanceledException>(() => connection.CopyTo(sql, destination));
                }
                Assert.Equal(System.Data.ConnectionState.Open, connection.State);
                if (transaction is not null) { await transaction.RollbackAsync(); }
            }
            Assert.Equal(backend, await identify.ExecuteScalarAsync<int>());
        }
        Assert.Equal(1, source.GetPoolStatistics().Opened);
        Assert.Equal(0, source.GetPoolStatistics().Discarded);
        Assert.Equal(1, source.GetPoolStatistics().Idle);
    }

    private sealed class CancelledDestination(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private static async Task ExecuteBlockedAsync(BlueTuskConnection connection, string kind, CancellationToken token)
    {
        var value = new string('x', 16 * 1024 * 1024);
        await using var command = new BlueTuskCommand("SELECT length('" + value + "')::int4", connection);
        switch (kind)
        {
            case "query":
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    _ = await reader.ReadAsync(token);
                }
                break;
            case "non-query":
                _ = await command.ExecuteNonQueryAsync(token);
                break;
            case "scalar":
                // A parameter forces the dedicated scalar response path.
                command.CommandText = "SELECT length(@value)::int4";
                command.Parameters.Add(new BlueTuskParameter<string>(value) { ParameterName = "value" });
                _ = await command.ExecuteScalarAsync<int>(token);
                break;
            case "pipeline":
                await using (var batch = connection.CreateBatch())
                {
                    batch.BatchCommands.Add(command.CommandText);
                    batch.BatchCommands.Add("SELECT 1");
                    _ = await batch.ExecuteNonQueryAsync(token);
                }
                break;
            case "binary-import":
                await using (var importer = await connection.BeginBinaryImportAsync(
                    "/*" + value + "*/ COPY bluetusk_stalled_import FROM STDIN WITH (FORMAT BINARY)", token))
                {
                    // No rows are sent; only startup delivery is exercised.
                }
                break;
            case "binary-export":
                await using (var exporter = await connection.BeginBinaryExportAsync(
                    "/*" + value + "*/ COPY (SELECT 1) TO STDOUT WITH (FORMAT BINARY)", token))
                {
                    // No result rows are read; only startup delivery is exercised.
                }
                break;
            case "copy-in":
                _ = await connection.CopyFromAsync(
                    "/*" + value + "*/ COPY bluetusk_stalled_import FROM STDIN", Stream.Null, token);
                break;
            case "copy-out":
                _ = await connection.CopyToAsync("/*" + value + "*/ COPY (SELECT 1) TO STDOUT", Stream.Null, token);
                break;
            case "binary-transfer":
                await using (var importer = await connection.BeginBinaryImportAsync(
                    "COPY bluetusk_stalled_import FROM STDIN WITH (FORMAT BINARY)", token))
                {
                    await importer.StartRowAsync(token);
                    await importer.WriteAsync(value, token);
                    _ = await importer.CompleteAsync(token);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}

internal sealed class StalledDeliveryProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopped = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly TaskCompletionSource _partial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accept;
    private int _armed;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Task PartialWrite => _partial.Task;
    public int DeclaredBytes { get; private set; }
    public int ForwardedBytes { get; private set; }
    public byte BlockedIdentifier { get; private set; }
    public void Arm() => Interlocked.Exchange(ref _armed, 1);
    public void Resume() => _resume.TrySetResult();

    public StalledDeliveryProxy(string host, int port)
    {
        _listener.Start();
        _accept = Accept(host, port);
    }

    private async Task Accept(string host, int port)
    {
        try
        {
            while (!_stopped.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                client.ReceiveBufferSize = 4096;
                _connections.Add(Bridge(client, host, port));
            }
        }
        catch (OperationCanceledException) when (_stopped.IsCancellationRequested) { }
        catch (SocketException) when (_stopped.IsCancellationRequested) { }
    }

    private async Task Bridge(TcpClient client, string host, int port)
    {
        using (client)
        using (var backend = new TcpClient())
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stopped.Token))
        {
            try
            {
                await backend.ConnectAsync(host, port, lifetime.Token);
                var upstream = Forward(client.GetStream(), backend.GetStream(), lifetime.Token);
                var downstream = backend.GetStream().CopyToAsync(client.GetStream(), lifetime.Token);
                await Task.WhenAny(upstream, downstream);
                await lifetime.CancelAsync();
                client.Close(); backend.Close();
                try { await Task.WhenAll(upstream, downstream); }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task Forward(Stream client, Stream backend, CancellationToken token)
    {
        var buffer = new byte[8192];
        var frame = new FrontendFrame();
        while (true)
        {
            var count = await client.ReadAsync(buffer, token);
            if (count == 0) return;
            await backend.WriteAsync(buffer.AsMemory(0, count), token);
            if (Volatile.Read(ref _armed) == 0) continue;
            frame.Observe(buffer.AsSpan(0, count));
            if (frame.Declared <= 1024 * 1024 || frame.Remaining <= 0 || Interlocked.CompareExchange(ref _armed, 0, 1) != 1) continue;
            DeclaredBytes = frame.Declared;
            ForwardedBytes = frame.Declared - frame.Remaining;
            BlockedIdentifier = frame.Identifier;
            _partial.TrySetResult();
            await _resume.Task.WaitAsync(token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Resume();
        await _stopped.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stopped.Dispose();
    }

    private sealed class FrontendFrame
    {
        private readonly byte[] _header = new byte[5];
        private int _headerCount;
        public int Declared { get; private set; }
        public int Remaining { get; private set; }
        public byte Identifier { get; private set; }
        public void Observe(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                if (Remaining > 0)
                {
                    var count = Math.Min(Remaining, bytes.Length);
                    Remaining -= count; bytes = bytes[count..];
                    continue;
                }
                var take = Math.Min(5 - _headerCount, bytes.Length);
                bytes[..take].CopyTo(_header.AsSpan(_headerCount));
                _headerCount += take; bytes = bytes[take..];
                if (_headerCount != 5) continue;
                var length = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1));
                if (length < 4 || length > 64 * 1024 * 1024) throw new InvalidDataException("The proxy lost a frontend message boundary.");
                Declared = length;
                Identifier = _header[0];
                Remaining = length - 4;
                _headerCount = 0;
            }
        }
    }
}
