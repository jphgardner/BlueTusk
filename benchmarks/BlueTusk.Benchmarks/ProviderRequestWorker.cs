using System.Data;
using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Data.Notifications;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueTusk.Benchmarks;

#pragma warning disable CS0618 // Like-for-like comparison with Npgsql's large-object stream API.

internal sealed class ProviderRequestWorker(ProviderRequestFixture fixture, int workerIndex) : IAsyncDisposable
{
    private static readonly Guid TokenValue = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly int[] TypedIntegers = [1, 2, 3, 5, 8, 13];
    private readonly byte[] _buffer = new byte[128 * 1024];
    private readonly string _channel = $"bt_capture_{Guid.NewGuid():N}";
    private DbConnection? _connection;
    private DbConnection? _listener;
    private DbCommand? _prepared;
    private IAsyncEnumerator<BlueTuskNotification>? _notifications;
    private CancellationTokenSource? _lifetime;
    private Task<bool>? _pendingNotification;
    private Task? _pendingNpgsqlWait;
    private TaskCompletionSource<string>? _notification;
    private uint _largeObject;
    private int _sequence;

    public static async Task<ProviderRequestWorker> CreateAsync(
        ProviderRequestFixture fixture, int workerIndex, CancellationToken token)
    {
        var worker = new ProviderRequestWorker(fixture, workerIndex);
        try
        {
            await worker.InitializeAsync(token);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var feature = fixture.Options.Feature;
        if (feature == "warm-pool-checkout" || feature.StartsWith("ef-", StringComparison.Ordinal))
        {
            return;
        }
        _connection = await fixture.Source.OpenConnectionAsync(token);
        if (feature is "prepared-scalar" or "prepared-typed-row")
        {
            _prepared = CreateCommand(feature == "prepared-scalar"
                ? "SELECT $1::int4 + 1"
                : "SELECT $1::int4, $2::text, $3::uuid, $4::numeric, $5::timestamptz, $6::int4[], $7::text::jsonb");
            AddParameter(_prepared, feature == "prepared-scalar" ? 41 : 42);
            if (feature == "prepared-typed-row")
            {
                AddParameter(_prepared, "BlueTusk 🐘");
                AddParameter(_prepared, TokenValue);
                AddParameter(_prepared, 12345.67m);
                AddParameter(_prepared, new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc));
                AddParameter(_prepared, TypedIntegers);
                AddParameter(_prepared, "{\"enabled\":true,\"count\":42}");
            }
            await _prepared.PrepareAsync(token);
        }
        if (feature == "notification-delivery")
        {
            if (_connection is BlueTuskConnection blue)
            {
                // ListenAsync owns its separate physical receive session already. Opening
                // another pooled connection here would consume three sockets per worker.
                await blue.ListenAsync(_channel, token);
                _notifications = blue.Notifications.GetAsyncEnumerator(_lifetime.Token);
            }
            else
            {
                _listener = await fixture.Source.OpenConnectionAsync(token);
                ((NpgsqlConnection)_listener).Notification += OnNotification;
                await ProviderRequestFixture.ExecuteAsync(_listener, $"LISTEN \"{_channel}\"", token);
            }
            _prepared = CreateCommand($"SELECT pg_notify('{_channel}', 'ready')");
        }
        if (feature == "large-object-read-1mib")
        {
            await using var transaction = await _connection.BeginTransactionAsync(token);
            if (_connection is BlueTuskConnection blue)
            {
                _largeObject = await blue.CreateLargeObjectAsync(token);
            }
            else
            {
                _largeObject = await new NpgsqlLargeObjectManager((NpgsqlConnection)_connection)
                    .CreateAsync(0, token);
            }
            await using (var stream = await OpenLargeObjectAsync(write: true, token))
            {
                var payload = new byte[1024 * 1024];
                for (var index = 0; index < payload.Length; index++)
                {
                    payload[index] = (byte)(index & 255);
                }
                await stream.WriteAsync(payload, token);
            }
            await transaction.CommitAsync(token);
        }
    }

    public async Task ExecuteCheckedAsync(CancellationToken token)
    {
        long result;
        long expected;
        switch (fixture.Options.Feature)
        {
            case "warm-pool-checkout":
                await using (var connection = await fixture.Source.OpenConnectionAsync(token))
                {
                    if (connection.State != ConnectionState.Open)
                    {
                        throw new InvalidOperationException("Pool returned a closed connection.");
                    }
                }
                return;
            case "parameterized-scalar":
                await using (var command = CreateCommand("SELECT $1::int4 + 1"))
                {
                    AddParameter(command, 41);
                    result = await ScalarAsync(command, token);
                }
                expected = 42;
                break;
            case "prepared-scalar":
                result = await ScalarAsync(_prepared!, token);
                expected = 42;
                break;
            case "sequential-1000-rows":
                result = 0;
                await using (var command = CreateCommand("SELECT value FROM generate_series(1, 1000) value"))
                await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        result += reader.GetInt32(0);
                    }
                }
                expected = 500_500;
                break;
            case "sequential-1mib-bytea":
                await using (var command = CreateCommand($"SELECT value FROM \"{fixture.Schema}\".payload"))
                await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
                {
                    if (!await reader.ReadAsync(token))
                    {
                        throw new InvalidOperationException("Payload row is missing.");
                    }
                    await using var stream = reader.GetStream(0);
                    result = await DrainAsync(stream, token);
                }
                expected = 1_048_576;
                break;
            case "empty-begin-rollback":
                await using (var transaction = await _connection!.BeginTransactionAsync(token))
                {
                    await transaction.RollbackAsync(token);
                }
                return;
            case "batch-16-scalars":
                result = await ExecuteBatchAsync(token);
                expected = 136;
                break;
            case "copy-import-1000":
                result = await ImportAsync(token);
                expected = 1000;
                break;
            case "copy-export-1000":
                result = await ExportAsync(token);
                expected = 499_500;
                break;
            case "prepared-typed-row":
                await using (var reader = await _prepared!.ExecuteReaderAsync(token))
                {
                    if (!await reader.ReadAsync(token))
                    {
                        throw new InvalidOperationException("Typed row is missing.");
                    }
                    result = reader.GetInt32(0) + reader.GetString(1).Length +
                        reader.GetGuid(2).ToByteArray()[0] + decimal.ToInt32(reader.GetDecimal(3)) +
                        reader.GetDateTime(4).Day + reader.GetFieldValue<int[]>(5).Sum() + reader.GetString(6).Length;
                }
                expected = 12_534;
                break;
            case "notification-delivery":
                await ReceiveNotificationAsync(token);
                return;
            case "large-object-read-1mib":
                await using (var transaction = await _connection!.BeginTransactionAsync(token))
                {
                    await using (var stream = await OpenLargeObjectAsync(write: false, token))
                    {
                        result = await DrainAsync(stream, token);
                    }
                    await transaction.RollbackAsync(token);
                }
                expected = 1_048_576;
                break;
            default:
                result = await ExecuteEfAsync(token);
                expected = fixture.Options.Feature switch
                {
                    "ef-compiled-query" => 450,
                    "ef-materialize-100" => 100,
                    _ => 1,
                };
                break;
        }
        if (result != expected)
        {
            throw new InvalidOperationException($"{fixture.Options.Feature} returned {result}; expected {expected}.");
        }
    }

    private DbCommand CreateCommand(string sql)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter<T>(DbCommand command, T value)
    {
        if (command is BlueTuskCommand blue)
        {
            blue.Parameters.Add(new BlueTuskParameter<T>(value));
        }
        else
        {
            ((NpgsqlCommand)command).Parameters.AddWithValue(value!);
        }
    }

    private static async Task<int> ScalarAsync(DbCommand command, CancellationToken token) =>
        command is BlueTuskCommand blue ? await blue.ExecuteScalarAsync<int>(token) :
        (int)(await command.ExecuteScalarAsync(token))!;

    private async Task<long> DrainAsync(Stream stream, CancellationToken token)
    {
        long bytes = 0;
        int count;
        while ((count = await stream.ReadAsync(_buffer, token)) != 0)
        {
            bytes += count;
        }
        return bytes;
    }

    private async Task<int> ExecuteBatchAsync(CancellationToken token)
    {
        await using var batch = _connection!.CreateBatch();
        for (var index = 0; index < 16; index++)
        {
            var command = batch.CreateBatchCommand();
            command.CommandText = "SELECT $1::int4 + 1";
            command.Parameters.Add(fixture.IsBlueTusk
                ? new BlueTuskParameter<int>(index)
                : new NpgsqlParameter { Value = index });
            batch.BatchCommands.Add(command);
        }
        await using var reader = await batch.ExecuteReaderAsync(token);
        var sum = 0;
        for (var index = 0; index < 16; index++)
        {
            if (!await reader.ReadAsync(token))
            {
                throw new InvalidOperationException("Batch row is missing.");
            }
            sum += reader.GetInt32(0);
            if (index != 15 && !await reader.NextResultAsync(token))
            {
                throw new InvalidOperationException("Batch result set is missing.");
            }
        }
        return sum;
    }

    private async Task<long> ImportAsync(CancellationToken token)
    {
        var sql = $"COPY \"{fixture.Schema}\".bulk (id, name, active, token) FROM STDIN WITH (FORMAT BINARY)";
        await using var transaction = await _connection!.BeginTransactionAsync(token);
        long count;
        if (_connection is BlueTuskConnection blue)
        {
            await using var importer = await blue.BeginBinaryImportAsync(sql, cancellationToken: token);
            for (var index = 0; index < 1000; index++)
            {
                await importer.StartRowAsync(token);
                await importer.WriteAsync(index, token);
                await importer.WriteAsync("benchmark-row", token);
                await importer.WriteAsync((index & 1) == 0, token);
                await importer.WriteAsync(TokenValue, token);
            }
            count = await importer.CompleteAsync(token);
        }
        else
        {
            await using var importer = await ((NpgsqlConnection)_connection).BeginBinaryImportAsync(sql, token);
            for (var index = 0; index < 1000; index++)
            {
                await importer.StartRowAsync(token);
                await importer.WriteAsync(index, token);
                await importer.WriteAsync("benchmark-row", token);
                await importer.WriteAsync((index & 1) == 0, token);
                await importer.WriteAsync(TokenValue, token);
            }
            count = checked((long)await importer.CompleteAsync(token));
        }
        await transaction.RollbackAsync(token);
        return count;
    }

    private async Task<long> ExportAsync(CancellationToken token)
    {
        const string sql = "COPY (SELECT value::int4, 'benchmark-row'::text, (value & 1) = 0, " +
            "'00112233-4455-6677-8899-aabbccddeeff'::uuid FROM generate_series(0, 999) value) TO STDOUT WITH (FORMAT BINARY)";
        long sum = 0;
        if (_connection is BlueTuskConnection blue)
        {
            await using var exporter = await blue.BeginBinaryExportAsync(sql, cancellationToken: token);
            while (await exporter.StartRowAsync(token) != -1)
            {
                sum += await exporter.ReadAsync<int>(token);
                _ = await exporter.ReadAsync<string>(token);
                _ = await exporter.ReadAsync<bool>(token);
                _ = await exporter.ReadAsync<Guid>(token);
            }
        }
        else
        {
            await using var exporter = await ((NpgsqlConnection)_connection!).BeginBinaryExportAsync(sql, token);
            while (await exporter.StartRowAsync(token) != -1)
            {
                sum += await exporter.ReadAsync<int>(token);
                _ = await exporter.ReadAsync<string>(token);
                _ = await exporter.ReadAsync<bool>(token);
                _ = await exporter.ReadAsync<Guid>(token);
            }
        }
        return sum;
    }

    private async Task<Stream> OpenLargeObjectAsync(bool write, CancellationToken token)
    {
        if (_connection is BlueTuskConnection blue)
        {
            return await blue.OpenLargeObjectAsync(_largeObject,
                write ? FileAccess.Write : FileAccess.Read, cancellationToken: token);
        }
        var manager = new NpgsqlLargeObjectManager((NpgsqlConnection)_connection!);
        return write ? await manager.OpenReadWriteAsync(_largeObject, token) :
            await manager.OpenReadAsync(_largeObject, token);
    }

    private async Task ReceiveNotificationAsync(CancellationToken token)
    {
        if (fixture.IsBlueTusk)
        {
            _pendingNotification = _notifications!.MoveNextAsync().AsTask();
            _ = await _prepared!.ExecuteNonQueryAsync(token);
            if (!await _pendingNotification.WaitAsync(token) || _notifications.Current.Payload != "ready")
            {
                throw new InvalidOperationException("Notification payload is missing or incorrect.");
            }
            _pendingNotification = null;
        }
        else
        {
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _notification, received);
            _pendingNpgsqlWait = ((NpgsqlConnection)_listener!).WaitAsync(token);
            _ = await _prepared!.ExecuteNonQueryAsync(token);
            await _pendingNpgsqlWait;
            _pendingNpgsqlWait = null;
            if (await received.Task.WaitAsync(token) != "ready")
            {
                throw new InvalidOperationException("Notification payload is incorrect.");
            }
            Volatile.Write(ref _notification, null);
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args) =>
        Volatile.Read(ref _notification)?.TrySetResult(args.Payload);

    private async Task<int> ExecuteEfAsync(CancellationToken token)
    {
        await using var context = fixture.CreateContext();
        switch (fixture.Options.Feature)
        {
            case "ef-compiled-query":
                var result = 0;
                await foreach (var id in fixture.CompiledQuery!(context, 450).WithCancellation(token))
                {
                    result = id;
                }
                return result;
            case "ef-materialize-100":
                return (await context.Orders.AsNoTracking().Where(order => order.Id >= 450 && order.Id < 550)
                    .OrderBy(order => order.Id).ToListAsync(token)).Count;
            case "ef-insert":
            case "ef-update":
                await using (var transaction = await context.Database.BeginTransactionAsync(token))
                {
                    if (fixture.Options.Feature == "ef-insert")
                    {
                        context.Orders.Add(new ProviderRequestFixture.Order
                        {
                            Customer = $"insert-{workerIndex}-{++_sequence}",
                            Total = 42.50m,
                            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        });
                    }
                    else
                    {
                        // Disjoint worker keys measure provider concurrency, not one hot-row lock.
                        var order = await context.Orders.SingleAsync(item => item.Id == workerIndex + 1, token);
                        order.Customer = "updated";
                    }
                    var changed = await context.SaveChangesAsync(token);
                    await transaction.RollbackAsync(token);
                    return changed;
                }
            default:
                throw new InvalidOperationException("Unsupported provider workload.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (_lifetime is not null) await _lifetime.CancelAsync();
            if (_pendingNotification is not null)
            {
                try { await _pendingNotification; }
                catch (OperationCanceledException) when (_lifetime!.IsCancellationRequested) { }
            }
            if (_pendingNpgsqlWait is not null)
            {
                try { await _pendingNpgsqlWait; }
                catch (OperationCanceledException) { }
            }
            if (_notifications is not null)
            {
                await _notifications.DisposeAsync();
            }
            if (_listener is NpgsqlConnection npgsql)
            {
                npgsql.Notification -= OnNotification;
            }
            if (_largeObject != 0 && _connection is not null)
            {
                if (_connection is BlueTuskConnection blue)
                {
                    await blue.DeleteLargeObjectAsync(_largeObject, timeout.Token);
                }
                else
                {
                    await using var transaction = await _connection.BeginTransactionAsync(timeout.Token);
                    await new NpgsqlLargeObjectManager((NpgsqlConnection)_connection).UnlinkAsync(_largeObject, timeout.Token);
                    await transaction.CommitAsync(timeout.Token);
                }
            }
        }
        finally
        {
            try
            {
                if (_prepared is not null) await _prepared.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (_listener is not null) await _listener.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        if (_connection is not null) await _connection.DisposeAsync();
                    }
                    finally { _lifetime?.Dispose(); }
                }
            }
        }
    }
}

#pragma warning restore CS0618
