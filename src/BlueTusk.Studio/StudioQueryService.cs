using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Sql;
using BlueTusk.Data;
using BlueTusk.Schema;

namespace BlueTusk.Studio;

public sealed class StudioQueryService : IDisposable
{
    private readonly StudioOptions _options;
    private readonly SemaphoreSlim _capacity;

    public StudioQueryService(StudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _capacity = new(options.MaximumConcurrentQueries);
    }

    public async ValueTask<StudioQueryResult> ExecuteAsync(StudioDatabaseScope scope, StudioQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(scope.DataSource);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);
        if (Encoding.UTF8.GetByteCount(request.Sql) > _options.MaximumRequestBytes)
        {
            throw new ArgumentException("The Studio request exceeds its configured SQL byte bound.", nameof(request));
        }
        var maximumRows = request.MaximumRows ?? _options.MaximumRows;
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, _options.MaximumRows);
        var sql = PostgreSqlStatementGuard.AdmitReadQuery(request.Sql);
        if (!await _capacity.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw new StudioCapacityException(); }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
            var token = deadline.Token;
            await using var connection = await scope.DataSource.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
            await using (var setup = connection.CreateCommand())
            {
                setup.Transaction = transaction;
                setup.CommandTimeout = _options.QueryTimeoutSeconds;
                setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL standard_conforming_strings = on; " +
                    "SET LOCAL statement_timeout = '" + (_options.QueryTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture) + "ms'";
                _ = await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _options.QueryTimeoutSeconds;
            var boundedSql = "SELECT * FROM (\n" + sql + "\n) AS bluetusk_studio_query LIMIT " + maximumRows.ToString(CultureInfo.InvariantCulture);
            command.CommandText = request.Explain ? "EXPLAIN (FORMAT JSON) " + boundedSql : boundedSql;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var result = await WriteResultAsync(reader, token).ConfigureAwait(false);
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }
        finally { _capacity.Release(); }
    }

    public void Dispose() => _capacity.Dispose();

    public async ValueTask<ReadOnlyMemory<byte>> CaptureSchemaAsync(StudioDatabaseScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!await _capacity.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw new StudioCapacityException(); }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
            var capture = new PostgreSqlSchemaCapture(scope.DataSource, new()
            {
                Schemas = scope.Schemas, MaximumMetadataBytes = _options.MaximumReplyBytes,
                CommandTimeoutSeconds = _options.QueryTimeoutSeconds,
            });
            return SchemaSnapshotSerializer.Serialize(await capture.CaptureAsync(deadline.Token).ConfigureAwait(false), _options.MaximumReplyBytes);
        }
        catch (SchemaCaptureLimitException) { throw new StudioReplyLimitException(); }
        finally { _capacity.Release(); }
    }

    private async ValueTask<StudioQueryResult> WriteResultAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        if (reader.FieldCount > 256) { throw new StudioReplyLimitException(); }
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteStartArray("columns");
        for (var index = 0; index < reader.FieldCount; index++)
        {
            writer.WriteStartObject();
            writer.WriteString("name", reader.GetName(index));
            writer.WriteString("type", reader.GetDataTypeName(index));
            writer.WriteEndObject();
            if (writer.BytesCommitted + writer.BytesPending > _options.MaximumReplyBytes) { throw new StudioReplyLimitException(); }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        var count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.WriteStartArray();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (reader is not BlueTuskDataReader boundedReader) { throw new NotSupportedException("Studio byte admission requires BlueTuskDataReader."); }
                if (boundedReader.GetFieldByteLength(index) > _options.MaximumReplyBytes) { throw new StudioReplyLimitException(); }
                WriteValue(writer, reader.GetValue(index));
                if (writer.BytesCommitted + writer.BytesPending > _options.MaximumReplyBytes) { throw new StudioReplyLimitException(); }
            }
            writer.WriteEndArray();
            count++;
        }
        writer.WriteEndArray();
        writer.WriteNumber("count", count);
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.Length > _options.MaximumReplyBytes) { throw new StudioReplyLimitException(); }
        return new(buffer.ToArray(), count);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case short number: writer.WriteNumberValue(number); break;
            case int number: writer.WriteNumberValue(number); break;
            // Render exact integer/decimal values as strings so browser floating-point
            // conversion cannot silently corrupt PostgreSQL values.
            case long number: writer.WriteStringValue(number.ToString(CultureInfo.InvariantCulture)); break;
            case decimal number: writer.WriteStringValue(number.ToString(CultureInfo.InvariantCulture)); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(number); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case DateTime date: writer.WriteStringValue(date); break;
            case DateTimeOffset date: writer.WriteStringValue(date); break;
            case Guid id: writer.WriteStringValue(id); break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }
}
