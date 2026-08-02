using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSessionIntegrationTests
{
    [Fact]
    public void Synchronous_session_uses_native_startup_and_extended_protocol_paths()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        using var session = BlueTuskSession.Open(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            });

        var simple = session.ExecuteSimpleQuery("SELECT 40::int4");
        Assert.Equal(
            "40",
            Encoding.UTF8.GetString(
                Assert.Single(Assert.Single(simple.ResultSets).Rows).Values[0]!.Value.Span));

        var value = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, 41);
        var extended = session.ExecuteExtendedQuery(
            "SELECT $1::int4 + 1",
            [new BlueTuskExtendedQueryParameter(23, 1, value)]);
        Assert.Equal(
            42,
            BinaryPrimitives.ReadInt32BigEndian(
                Assert.Single(Assert.Single(extended.ResultSets).Rows).Values[0]!.Value.Span));

        const string statementName = "bluetusk_sync_prepared";
        session.PrepareStatement(statementName, "SELECT $1::int4 + 1", [23]);
        var prepared = session.ExecutePreparedStatement(
            statementName,
            [new BlueTuskExtendedQueryParameter(23, 1, value)]);
        Assert.Equal(
            42,
            BinaryPrimitives.ReadInt32BigEndian(
                Assert.Single(Assert.Single(prepared.ResultSets).Rows).Values[0]!.Value.Span));
        session.ClosePreparedStatement(statementName);

        var batch = session.ExecuteBatch(
            [
                new BlueTuskBatchQuery("SELECT 1", [], UseBinaryResults: false),
                new BlueTuskBatchQuery("SELECT 2", [], UseBinaryResults: false),
            ]);
        Assert.Equal(2, batch.ResultSets.Count);

        var pipeline = session.ExecutePipeline(
        [
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 1", [], UseBinaryResults: false),
                new BlueTuskBatchQuery("SELECT 2", [], UseBinaryResults: false),
            ]),
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 1::int4 / 0::int4", [], UseBinaryResults: false),
            ]),
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 3", [], UseBinaryResults: false),
            ]),
        ]);

        Assert.True(pipeline.Groups[0].Succeeded);
        Assert.Equal(2, pipeline.Groups[0].Result.ResultSets.Count);
        Assert.Equal("22012", pipeline.Groups[1].Error!.SqlState);
        Assert.True(pipeline.Groups[2].Succeeded);
        Assert.Equal(
            "3",
            Encoding.UTF8.GetString(
                Assert.Single(Assert.Single(pipeline.Groups[2].Result.ResultSets).Rows).Values[0]!.Value.Span));
        Assert.True(session.Capabilities.SupportsPipelineMode);
    }

    [Fact]
    public async Task Pipeline_groups_preserve_order_and_continue_after_a_group_error()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            });

        var pipeline = await session.ExecutePipelineAsync(
        [
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 10::int4", [], UseBinaryResults: false),
                new BlueTuskBatchQuery("SELECT 20::int4", [], UseBinaryResults: false),
            ]),
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT missing_pipeline_column", [], UseBinaryResults: false),
                new BlueTuskBatchQuery("SELECT 99::int4", [], UseBinaryResults: false),
            ]),
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 30::int4", [], UseBinaryResults: false),
            ]),
        ]);

        Assert.Equal(3, pipeline.Groups.Count);
        Assert.Equal(["10", "20"], pipeline.Groups[0].Result.ResultSets.Select(ReadSingleText));
        Assert.Equal("42703", pipeline.Groups[1].Error!.SqlState);
        Assert.Empty(pipeline.Groups[1].Result.ResultSets);
        Assert.Equal("30", ReadSingleText(Assert.Single(pipeline.Groups[2].Result.ResultSets)));
        Assert.False(pipeline.Succeeded);

        var reused = await session.ExecuteSimpleQueryAsync("SELECT 42::int4");
        Assert.Equal("42", ReadSingleText(Assert.Single(reused.ResultSets)));
    }

    [Fact]
    public async Task Cancelling_a_pipeline_drains_already_sent_groups_before_session_reuse()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            });
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecutePipelineAsync(
        [
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT pg_sleep(10)", [], UseBinaryResults: false),
            ]),
            new BlueTuskPipelineGroup(
            [
                new BlueTuskBatchQuery("SELECT 41::int4", [], UseBinaryResults: false),
            ]),
        ], cancellationSource.Token).AsTask());

        var reused = await session.ExecuteSimpleQueryAsync("SELECT 42::int4");
        Assert.Equal("42", ReadSingleText(Assert.Single(reused.ResultSets)));
    }

    [Fact]
    public async Task Opens_with_scram_and_executes_a_simple_query()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);

        var result = await session.ExecuteSimpleQueryAsync(
            "SELECT 42::int4 AS answer, 'hello'::text AS greeting, NULL::text AS missing",
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(["answer", "greeting", "missing"], resultSet.Fields.Select(field => field.Name));
        Assert.Equal("42", Encoding.UTF8.GetString(row.Values[0]!.Value.Span));
        Assert.Equal("hello", Encoding.UTF8.GetString(row.Values[1]!.Value.Span));
        Assert.Null(row.Values[2]);
        Assert.Equal("SELECT 1", resultSet.CommandTag);
        Assert.Contains("server_version", session.Parameters);
        Assert.NotNull(session.BackendKeyData);
    }

    [Fact]
    public async Task Session_executes_an_extended_query_with_binary_parameters()
    {
        var connectionString = GetConnectionString();
        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);
        var left = new byte[sizeof(int)];
        var right = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(left, 20);
        BinaryPrimitives.WriteInt32BigEndian(right, 22);

        var result = await session.ExecuteExtendedQueryAsync(
            "SELECT $1::int4 + $2::int4 AS answer",
            [
                new BlueTuskExtendedQueryParameter(23, 1, left),
                new BlueTuskExtendedQueryParameter(23, 1, right),
            ],
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(1, Assert.Single(resultSet.Fields).FormatCode);
        Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(row.Values[0]!.Value.Span));
    }

    [Fact]
    public async Task Session_prepares_executes_and_closes_a_named_statement()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);
        const string statementName = "bluetusk_integration_prepared";

        await session.PrepareStatementAsync(
            statementName,
            "SELECT $1::int4 + $2::int4 AS answer",
            [23, 23],
            CancellationToken.None);

        var left = new byte[sizeof(int)];
        var right = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(left, 19);
        BinaryPrimitives.WriteInt32BigEndian(right, 23);
        var result = await session.ExecutePreparedStatementAsync(
            statementName,
            [
                new BlueTuskExtendedQueryParameter(23, 1, left),
                new BlueTuskExtendedQueryParameter(23, 1, right),
            ],
            cancellationToken: CancellationToken.None);

        var row = Assert.Single(Assert.Single(result.ResultSets).Rows);
        Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(row.Values[0]!.Value.Span));

        await session.ClosePreparedStatementAsync(statementName, CancellationToken.None);
        var prepared = await session.ExecuteSimpleQueryAsync(
            $"SELECT count(*) FROM pg_prepared_statements WHERE name = '{statementName}'",
            CancellationToken.None);
        Assert.Equal(
            "0",
            Encoding.UTF8.GetString(
                Assert.Single(Assert.Single(prepared.ResultSets).Rows).Values[0]!.Value.Span));
    }

    [Fact]
    public async Task AdoNet_prepare_reuses_and_reprepares_named_statements()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        var left = new BlueTuskParameter<int>(20);
        var right = new BlueTuskParameter<int>(22);
        await using var command = new BlueTuskCommand(
            "SELECT $1::int4 + $2::int4",
            connection);
        command.Parameters.Add(left);
        command.Parameters.Add(right);

        await command.PrepareAsync(CancellationToken.None);
        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));

        left.TypedValue = 6;
        right.TypedValue = 7;
        Assert.Equal(13, await command.ExecuteScalarAsync<int>(CancellationToken.None));

        command.CommandText = "SELECT $1::int4 * $2::int4";
        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));

        await using var count = new BlueTuskCommand(
            "SELECT count(*) FROM pg_prepared_statements WHERE name LIKE 'bluetusk_%'",
            connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_automatically_prepares_evicts_and_invalidates_statements()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            Pooling = false,
            MaxAutoPrepare = 2,
            AutoPrepareMinUsages = 2,
        };
        await using var connection = new BlueTuskConnection(settings.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await ExecuteAsync("SELECT $1::int4 + 1", 41);
        Assert.Equal(0L, await CountPreparedAsync());
        await ExecuteAsync("SELECT $1::int4 + 1", 41);
        Assert.Equal(1L, await CountPreparedAsync());

        await ExecuteAsync("SELECT $1::int4 + 2", 40);
        await ExecuteAsync("SELECT $1::int4 + 2", 40);
        await ExecuteAsync("SELECT $1::int4 + 1", 41);
        await ExecuteAsync("SELECT $1::int4 + 3", 39);
        await ExecuteAsync("SELECT $1::int4 + 3", 39);

        await using (var statements = new BlueTuskCommand(
            "SELECT statement FROM pg_prepared_statements ORDER BY statement",
            connection))
        await using (var reader = await statements.ExecuteReaderAsync(CancellationToken.None))
        {
            var preparedSql = new List<string>();
            while (await reader.ReadAsync(CancellationToken.None))
            {
                preparedSql.Add(reader.GetString(0));
            }

            Assert.Equal(["SELECT $1::int4 + 1", "SELECT $1::int4 + 3"], preparedSql);
        }

        await using (var deallocate = new BlueTuskCommand("DEALLOCATE ALL", connection))
        {
            _ = await deallocate.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await ExecuteAsync("SELECT $1::int4 + 1", 41);
        Assert.Equal(0L, await CountPreparedAsync());
        await ExecuteAsync("SELECT $1::int4 + 1", 41);
        Assert.Equal(1L, await CountPreparedAsync());

        async Task ExecuteAsync(string sql, int value)
        {
            await using var command = new BlueTuskCommand(sql, connection);
            command.Parameters.Add(new BlueTuskParameter<int>(value));
            Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
        }

        async Task<long> CountPreparedAsync()
        {
            await using var command = new BlueTuskCommand(
                "SELECT count(*) FROM pg_prepared_statements WHERE name LIKE 'bluetusk_auto_%'",
                connection);
            return await command.ExecuteScalarAsync<long>(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Session_waits_for_notifications_and_remains_usable_after_wait_cancellation()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        var options = new BlueTuskClientOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        await using var listener = await BlueTuskSession.OpenAsync(options, CancellationToken.None);
        await using var publisher = await BlueTuskSession.OpenAsync(options, CancellationToken.None);

        _ = await listener.ExecuteSimpleQueryAsync(
            "LISTEN bluetusk_client_notifications",
            CancellationToken.None);
        var pending = listener.WaitForNotificationAsync(CancellationToken.None).AsTask();
        _ = await publisher.ExecuteSimpleQueryAsync(
            "SELECT pg_notify('bluetusk_client_notifications', 'created \U0001F9A3')",
            CancellationToken.None);

        var notification = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("bluetusk_client_notifications", notification.Channel);
        Assert.Equal("created \U0001F9A3", notification.Payload);
        Assert.Equal(publisher.BackendKeyData!.Value.ProcessId, notification.ProcessId);

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => listener.WaitForNotificationAsync(cancellationSource.Token).AsTask());

        var result = await listener.ExecuteSimpleQueryAsync("SELECT 42", CancellationToken.None);
        Assert.Equal("SELECT 1", Assert.Single(result.ResultSets).CommandTag);
    }

    [Theory]
    [InlineData(BlueTuskReplicationMode.Physical)]
    [InlineData(BlueTuskReplicationMode.Database)]
    public async Task Session_negotiates_replication_startup_modes(
        BlueTuskReplicationMode replicationMode)
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
                ReplicationMode = replicationMode,
            },
            CancellationToken.None);

        var result = await session.ExecuteSimpleQueryAsync(
            "IDENTIFY_SYSTEM",
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        Assert.Equal(
            ["systemid", "timeline", "xlogpos", "dbname"],
            resultSet.Fields.Select(static field => field.Name));
        Assert.Single(resultSet.Rows);
    }

    [Fact]
    public async Task Session_streams_duplex_copy_data_and_returns_to_ready()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
                ReplicationMode = BlueTuskReplicationMode.Physical,
            },
            CancellationToken.None);

        var identity = await session.ExecuteSimpleQueryAsync(
            "IDENTIFY_SYSTEM",
            CancellationToken.None);
        var identityRow = Assert.Single(Assert.Single(identity.ResultSets).Rows);
        var walPosition = Encoding.UTF8.GetString(identityRow.Values[2]!.Value.Span);

        await using var channel = await session.BeginCopyBothAsync(
            $"START_REPLICATION PHYSICAL {walPosition}",
            CancellationToken.None);

        await using (var writer = new BlueTuskConnection(GetConnectionString()))
        {
            await writer.OpenAsync(CancellationToken.None);
            await using var command = new BlueTuskCommand("SELECT pg_switch_wal()", writer);
            _ = await command.ExecuteScalarAsync(CancellationToken.None);
        }

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ReadOnlyMemory<byte>? payload;
        do
        {
            payload = await channel.ReadAsync(readTimeout.Token);
        }
        while (payload is { Length: > 0 } value && value.Span[0] != (byte)'w');

        Assert.NotNull(payload);
        Assert.Equal((byte)'w', payload.Value.Span[0]);

        _ = await channel.CompleteAsync();
        Assert.True(channel.IsCompleted);

        var reused = await session.ExecuteSimpleQueryAsync(
            "IDENTIFY_SYSTEM",
            CancellationToken.None);
        Assert.Single(Assert.Single(reused.ResultSets).Rows);
    }

    [Fact]
    public async Task Session_cancels_and_drains_before_reuse()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ExecuteSimpleQueryAsync("SELECT pg_sleep(10)", cancellationSource.Token).AsTask());

        var result = await session.ExecuteSimpleQueryAsync("SELECT 42::int4", CancellationToken.None);
        var row = Assert.Single(Assert.Single(result.ResultSets).Rows);
        Assert.Equal("42", Encoding.UTF8.GetString(row.Values[0]!.Value.Span));
    }

    [Fact]
    public async Task Command_cancellation_tokens_preserve_the_connection()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand("SELECT pg_sleep(10)", connection);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteNonQueryAsync(cancellationSource.Token));

        Assert.Equal(ConnectionState.Open, connection.State);
        await using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Command_timeouts_cancel_on_the_server_and_preserve_the_connection()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand("SELECT pg_sleep(10)", connection)
        {
            CommandTimeout = 1,
        };

        _ = await Assert.ThrowsAsync<TimeoutException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Equal(ConnectionState.Open, connection.State);
        await using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Prepared_command_timeout_reuses_the_outstanding_deadline_wakeup()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand(
            "SELECT CASE WHEN @delay THEN pg_sleep(10) END",
            connection)
        {
            CommandTimeout = 1,
        };
        var delay = new BlueTuskParameter<bool>(false) { ParameterName = "delay" };
        command.Parameters.Add(delay);
        await command.PrepareAsync(CancellationToken.None);

        for (var execution = 0; execution < 3; execution++)
        {
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        delay.Value = true;
        _ = await Assert.ThrowsAsync<TimeoutException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Equal(ConnectionState.Open, connection.State);
        await using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_command_cancellation_uses_the_dedicated_channel(bool asynchronous)
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand("SELECT pg_sleep(10)", connection);
        var execution = command.ExecuteNonQueryAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        if (asynchronous)
        {
            await command.CancelAsync(CancellationToken.None);
        }
        else
        {
            command.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_inside_a_transaction_preserves_failed_state_for_rollback()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using var sleeping = new BlueTuskCommand("SELECT pg_sleep(10)", connection)
        {
            CommandTimeout = 1,
            Transaction = transaction,
        };

        _ = await Assert.ThrowsAsync<TimeoutException>(
            () => sleeping.ExecuteNonQueryAsync(CancellationToken.None));

        await using var failed = new BlueTuskCommand("SELECT 1::int4", connection)
        {
            Transaction = transaction,
        };
        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => failed.ExecuteScalarAsync(CancellationToken.None));
        Assert.Equal("25P02", exception.SqlState);

        await transaction.RollbackAsync(CancellationToken.None);
        await using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_connection_command_and_reader_execute_end_to_end()
    {
        var connectionString = GetConnectionString();
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand(
            "SELECT 42::int4 AS answer, 'hello'::text AS greeting, NULL::text AS missing, current_timestamp AS now, point(1, 2) AS point_value; SELECT 7::int4 AS second",
            connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(42, reader.GetInt32(reader.GetOrdinal("answer")));
        Assert.Equal("hello", reader.GetString(1));
        Assert.True(await reader.IsDBNullAsync(2, CancellationToken.None));
        Assert.IsType<DateTimeOffset>(reader.GetValue(3));
        Assert.Equal(new BlueTuskPoint(1, 2), reader.GetFieldValue<BlueTuskPoint>(4));
        Assert.False(await reader.ReadAsync(CancellationToken.None));
        Assert.True(await reader.NextResultAsync(CancellationToken.None));
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(7, reader.GetInt32(0));
        Assert.False(await reader.NextResultAsync(CancellationToken.None));
        Assert.NotEmpty(connection.ServerVersion);
    }

    [Fact]
    public async Task AdoNet_typed_scalar_decodes_int4()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand("SELECT 6::int4 * 7::int4", connection);

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_async_object_scalar_preserves_database_null()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand("SELECT NULL::int4", connection);

        Assert.Same(DBNull.Value, await command.ExecuteScalarAsync(CancellationToken.None));
        Assert.Null(await command.ExecuteScalarAsync<int?>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_parameters_execute_through_the_extended_protocol()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
        command.Parameters.Add(new BlueTuskParameter<int>(20));
        command.Parameters.Add(new BlueTuskParameter<int>(22));

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_named_parameters_are_rewritten_and_can_be_prepared()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new BlueTuskCommand(
            "SELECT @left::int4 + :right::int4 + @LEFT::int4",
            connection);
        command.Parameters.Add(new BlueTuskParameter<int>(22) { ParameterName = "right" });
        command.Parameters.Add(new BlueTuskParameter<int>(10) { ParameterName = "left" });

        await command.PrepareAsync(CancellationToken.None);

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_execution_mode_selects_extended_and_guards_simple_commands()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var extended = new BlueTuskCommand("SELECT 42::int4", connection)
        {
            ExecutionMode = BlueTuskCommandExecutionMode.Extended,
        };

        Assert.Equal(42, await extended.ExecuteScalarAsync<int>(CancellationToken.None));

        await using var simple = new BlueTuskCommand("SELECT $1::int4", connection)
        {
            ExecutionMode = BlueTuskCommandExecutionMode.Simple,
        };
        simple.Parameters.Add(new BlueTuskParameter<int>(42));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => simple.ExecuteScalarAsync(CancellationToken.None));
        Assert.Contains("Simple execution mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdoNet_parameter_values_are_not_interpolated_into_sql()
    {
        const string injectionShapedValue = "'; DROP TABLE important_data; --";
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::text");
        command.Parameters.Add(new BlueTuskParameter<string>(injectionShapedValue));

        Assert.Equal(injectionShapedValue, await command.ExecuteScalarAsync<string>(CancellationToken.None));
    }

    [Fact]
    public async Task AdoNet_null_parameters_use_the_declared_type()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::text IS NULL");
        command.Parameters.Add(new BlueTuskParameter(DBNull.Value) { DbType = DbType.String });

        Assert.True(await command.ExecuteScalarAsync<bool>(CancellationToken.None));
    }

    [Fact]
    public async Task Data_source_commands_own_their_physical_connection()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT 40::int4 + 2::int4");

        Assert.Equal(42, await command.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Errors_are_drained_through_ready_for_query()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var invalid = new BlueTuskCommand("SELEC broken", connection);

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => invalid.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Equal("42601", exception.SqlState);
        await using var valid = new BlueTuskCommand("SELECT 1::int4", connection);
        Assert.Equal(1, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Extended_query_errors_are_drained_through_ready_for_query()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var invalid = new BlueTuskCommand("SELECT $1::int4", connection);
        invalid.Parameters.Add(new BlueTuskParameter<string>("not-an-integer"));

        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => invalid.ExecuteNonQueryAsync(CancellationToken.None));

        Assert.Equal("22P02", exception.SqlState);
        await using var valid = new BlueTuskCommand("SELECT $1::int4", connection);
        valid.Parameters.Add(new BlueTuskParameter<int>(42));
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Transactions_commit_and_roll_back_on_one_connection()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var create = new BlueTuskCommand(
                         "CREATE TEMP TABLE bluetusk_transaction_test (value int4 NOT NULL)",
                         connection))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (var rollback = await connection.BeginTransactionAsync(
                         IsolationLevel.Serializable,
                         CancellationToken.None))
        {
            await using var isolation = new BlueTuskCommand("SHOW transaction_isolation", connection)
            {
                Transaction = rollback,
            };
            Assert.Equal("serializable", await isolation.ExecuteScalarAsync<string>(CancellationToken.None));

            await using var insert = new BlueTuskCommand(
                "INSERT INTO bluetusk_transaction_test (value) VALUES ($1)",
                connection)
            {
                Transaction = rollback,
            };
            insert.Parameters.Add(new BlueTuskParameter<int>(1));
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
            await rollback.RollbackAsync(CancellationToken.None);
        }

        await using (var count = new BlueTuskCommand("SELECT count(*)::int8 FROM bluetusk_transaction_test", connection))
        {
            Assert.Equal(0, await count.ExecuteScalarAsync<long>(CancellationToken.None));
        }

        await using (var commit = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            await using var insert = new BlueTuskCommand(
                "INSERT INTO bluetusk_transaction_test (value) VALUES ($1)",
                connection)
            {
                Transaction = commit,
            };
            insert.Parameters.Add(new BlueTuskParameter<int>(2));
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
            await commit.CommitAsync(CancellationToken.None);
        }

        await using (var count = new BlueTuskCommand("SELECT count(*)::int8 FROM bluetusk_transaction_test", connection))
        {
            Assert.Equal(1, await count.ExecuteScalarAsync<long>(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Commands_require_the_active_transaction_and_rollback_recovers_failures()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using var unenlisted = new BlueTuskCommand("SELECT 1::int4", connection);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unenlisted.ExecuteScalarAsync(CancellationToken.None));

        await using var invalid = new BlueTuskCommand("SELECT 1::int4 / 0::int4", connection)
        {
            Transaction = transaction,
        };
        var exception = await Assert.ThrowsAsync<BlueTuskException>(
            () => invalid.ExecuteScalarAsync(CancellationToken.None));
        Assert.Equal("22012", exception.SqlState);

        await transaction.RollbackAsync(CancellationToken.None);
        await using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Disposing_a_transaction_asynchronously_rolls_it_back()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var create = new BlueTuskCommand(
                         "CREATE TEMP TABLE bluetusk_dispose_transaction_test (value int4 NOT NULL)",
                         connection))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using (var insert = new BlueTuskCommand(
                         "INSERT INTO bluetusk_dispose_transaction_test (value) VALUES (1)",
                         connection)
        {
            Transaction = transaction,
        })
        {
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
        }

        await transaction.DisposeAsync();

        await using var count = new BlueTuskCommand(
            "SELECT count(*)::int8 FROM bluetusk_dispose_transaction_test",
            connection);
        Assert.Equal(0, await count.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task Transactions_work_through_ADO_NET_base_classes()
    {
        await using DbConnection connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42::int4";
        command.Transaction = transaction;

        Assert.Equal(42, await command.ExecuteScalarAsync(CancellationToken.None));
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Connection_string_security_information_is_not_persisted_by_default()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            Pooling = false,
        };
        var password = settings.Password ?? throw SkipException.ForSkip(
            "The configured integration connection does not contain a password.");

        await using var connection = new BlueTuskConnection(settings.ConnectionString);
        Assert.Equal(
            password,
            new BlueTuskConnectionStringBuilder(connection.ConnectionString).Password);

        await connection.OpenAsync(CancellationToken.None);
        Assert.Null(new BlueTuskConnectionStringBuilder(connection.ConnectionString).Password);
        await connection.CloseAsync();
        Assert.Null(new BlueTuskConnectionStringBuilder(connection.ConnectionString).Password);

        await connection.OpenAsync(CancellationToken.None);
        await connection.CloseAsync();

        using (var synchronousConnection = new BlueTuskConnection(settings.ConnectionString))
        {
            synchronousConnection.Open();
            Assert.Null(
                new BlueTuskConnectionStringBuilder(synchronousConnection.ConnectionString).Password);
        }

        settings.PersistSecurityInfo = true;
        await using var persistentConnection = new BlueTuskConnection(settings.ConnectionString);
        await persistentConnection.OpenAsync(CancellationToken.None);
        Assert.Equal(
            password,
            new BlueTuskConnectionStringBuilder(persistentConnection.ConnectionString).Password);
    }

    private static string ReadSingleText(BlueTuskResultSet resultSet) =>
        Encoding.UTF8.GetString(Assert.Single(resultSet.Rows).Values[0]!.Value.Span);

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
