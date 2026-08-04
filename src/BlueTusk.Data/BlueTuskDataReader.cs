using System.Buffers.Binary;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

[SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "DbDataReader defines the required non-generic ADO.NET enumeration contract.")]
[SuppressMessage(
    "Usage",
    "CA2201:Do not raise reserved exception types",
    Justification = "ADO.NET readers conventionally use IndexOutOfRangeException for missing columns.")]
public sealed class BlueTuskDataReader : DbDataReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly BlueTuskQueryResult? _result;
    private readonly BlueTuskTypeRegistry _types;
    private readonly bool _singleRow;
    private readonly BlueTuskCommand? _streamingCommand;
    private readonly BlueTuskCommandTimeout? _streamingTimeoutTimer;
    private readonly BlueTuskConnection? _executionConnection;
    private readonly BlueTuskFieldDescription[]? _streamingFields;
    private BlueTuskConnection? _connectionToClose;
    private BlueTuskPortal? _portal;
    private BlueTuskPortalRow? _streamingRow;
    private BlueTuskPortalRow? _prefetchedStreamingRow;
    private int _resultIndex;
    private int _rowIndex = -1;
    private long _streamingRowsReturned;
    private int _lifetimeCompleted;
    private bool _closed;

    internal BlueTuskDataReader(
        BlueTuskQueryResult result,
        BlueTuskConnection? connectionToClose,
        BlueTuskTypeRegistry types)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _connectionToClose = connectionToClose;
        _types = types ?? throw new ArgumentNullException(nameof(types));
    }

    internal BlueTuskDataReader(
        BlueTuskPortal portal,
        BlueTuskConnection executionConnection,
        BlueTuskConnection? connectionToClose,
        BlueTuskTypeRegistry types,
        bool singleRow,
        BlueTuskCommand streamingCommand,
        BlueTuskCommandTimeout? streamingTimeoutTimer)
    {
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _executionConnection = executionConnection ?? throw new ArgumentNullException(nameof(executionConnection));
        _connectionToClose = connectionToClose;
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _streamingFields = portal.Fields as BlueTuskFieldDescription[] ?? [.. portal.Fields];
        _singleRow = singleRow;
        _streamingCommand = streamingCommand ?? throw new ArgumentNullException(nameof(streamingCommand));
        _streamingTimeoutTimer = streamingTimeoutTimer;
    }

    public override int FieldCount => _streamingFields?.Length ?? CurrentFields.Count;

    public override bool HasRows => _portal is not null
        ? _streamingRowsReturned != 0 || PrefetchStreamingRow() is not null
        : CurrentResultSet?.Rows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => GetRecordsAffected();

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    private BlueTuskResultSet? CurrentResultSet =>
        _result is not null && _resultIndex < _result.ResultSets.Count
            ? _result.ResultSets[_resultIndex]
            : null;

    private IReadOnlyList<BlueTuskFieldDescription> CurrentFields =>
        _streamingFields ?? CurrentResultSet?.Fields ?? [];

    private BlueTuskDataRow CurrentRow =>
        CurrentResultSet is { } resultSet && _rowIndex >= 0 && _rowIndex < resultSet.Rows.Count
            ? resultSet.Rows[_rowIndex]
            : throw new InvalidOperationException("The reader is not positioned on a row.");

    public override bool Read()
    {
        EnsureOpen();
        if (_portal is not null)
        {
            return ReadStreaming();
        }

        var resultSet = CurrentResultSet;
        if (resultSet is null || _rowIndex + 1 >= resultSet.Rows.Count)
        {
            return false;
        }

        _rowIndex++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        if (_portal is null)
        {
            return Task.FromResult(Read());
        }

        if (_singleRow && _streamingRowsReturned != 0)
        {
            return DisposeSingleRowAsync();
        }

        if (_prefetchedStreamingRow is not null)
        {
            _streamingRow = _prefetchedStreamingRow;
            _prefetchedStreamingRow = null;
        }
        else
        {
            if (_portal.TryReadBuffered(out var bufferedRow))
            {
                _streamingRow = bufferedRow;
                if (_streamingRow is null)
                {
                    return Task.FromResult(false);
                }

                _streamingRowsReturned++;
                return Task.FromResult(true);
            }

            ValueTask<BlueTuskPortalRow?> pendingRow;
            try
            {
                pendingRow = _portal.ReadAsync(cancellationToken);
                if (!pendingRow.IsCompletedSuccessfully)
                {
                    return AwaitRowAsync(pendingRow);
                }

                _streamingRow = pendingRow.Result;
            }
            catch (BlueTuskServerException exception)
            {
                throw TranslateServerException(exception);
            }
            catch (Exception) when (_executionConnection is { HasOpenSession: false })
            {
                _executionConnection.Close();
                throw;
            }
        }

        if (_streamingRow is null)
        {
            return Task.FromResult(false);
        }

        _streamingRowsReturned++;
        return Task.FromResult(true);
    }

    private async Task<bool> DisposeSingleRowAsync()
    {
        await _portal!.DisposeAsync().ConfigureAwait(false);
        _streamingRow = null;
        return false;
    }

    private async Task<bool> AwaitRowAsync(ValueTask<BlueTuskPortalRow?> pendingRow)
    {
        try
        {
            _streamingRow = await pendingRow.ConfigureAwait(false);
            if (_streamingRow is null)
            {
                return false;
            }

            _streamingRowsReturned++;
            return true;
        }
        catch (BlueTuskServerException exception)
        {
            throw TranslateServerException(exception);
        }
        catch (Exception) when (_executionConnection is { HasOpenSession: false })
        {
            await _executionConnection.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override bool NextResult()
    {
        EnsureOpen();
        if (_portal is not null)
        {
            _portal.Dispose();
            _streamingRow = null;
            _prefetchedStreamingRow = null;
            return false;
        }

        if (_resultIndex + 1 >= _result!.ResultSets.Count)
        {
            return false;
        }

        _resultIndex++;
        _rowIndex = -1;
        return true;
    }

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        if (_portal is not null)
        {
            await _portal.DisposeAsync().ConfigureAwait(false);
            _streamingRow = null;
            _prefetchedStreamingRow = null;
            return false;
        }

        return NextResult();
    }

    public override string GetName(int ordinal) => GetField(ordinal).Name;

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var fields = CurrentFields;
        if (fields.Count == 0)
        {
            throw new InvalidOperationException("The reader has no current result.");
        }
        for (var index = 0; index < fields.Count; index++)
        {
            if (string.Equals(fields[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new IndexOutOfRangeException($"Column '{name}' was not found.");
    }

    public override string GetDataTypeName(int ordinal) =>
        BlueTuskValueDecoder.GetDataTypeName(_types, GetField(ordinal).TypeOid);

    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields |
        DynamicallyAccessedMemberTypes.PublicProperties)]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2073",
        Justification =
            "DbDataReader requires this annotation, but provider field types are runtime " +
            "catalogue metadata and BlueTusk does not reflect over the returned Type.")]
    public override Type GetFieldType(int ordinal) =>
        BlueTuskValueDecoder.GetFieldType(_types, GetField(ordinal));

    public override object GetValue(int ordinal)
    {
        var field = GetField(ordinal);
        var value = _portal is null
            ? CurrentRow.Values[ordinal]
            : GetStreamingRow().ReadField(ordinal);
        return BlueTuskValueDecoder.Decode(_types, field, value);
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => _portal is null
        ? CurrentRow.Values[ValidateOrdinal(ordinal)] is null
        : GetStreamingRow().IsDBNull(ValidateOrdinal(ordinal));

    public override async Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _portal is null
            ? IsDBNull(ordinal)
            : await GetStreamingRow().IsDBNullAsync(
                ValidateOrdinal(ordinal),
                cancellationToken).ConfigureAwait(false);
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        return ConvertFieldValue<T>(value);
    }

    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification =
            "The dynamic conversion fallback is reached only when dynamic code is available; " +
            "NativeAOT supports direct arrays and the explicitly rooted common conversions.")]
    private static T ConvertFieldValue<T>(object value)
    {
        if (value is DBNull)
        {
            throw new InvalidCastException("A database NULL cannot be read as a non-null value.");
        }

        if (value is T typed)
        {
            return typed;
        }

        if (value is Array array && typeof(T).IsArray)
        {
            if (typeof(T) == typeof(decimal[]))
            {
                return (T)(object)ConvertOneDimensionalArray<decimal>(array);
            }

            if (typeof(T) == typeof(TimeOnly[]))
            {
                return (T)(object)ConvertOneDimensionalArray<TimeOnly>(array);
            }

            if (typeof(T) == typeof(TimeSpan[]))
            {
                return (T)(object)ConvertOneDimensionalArray<TimeSpan>(array);
            }

            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                throw new NotSupportedException(
                    $"NativeAOT cannot dynamically convert {array.GetType().FullName} to " +
                    $"{typeof(T).FullName}. Request the codec-native array type, or use a " +
                    "one-dimensional decimal[], TimeOnly[] or TimeSpan[] conversion.");
            }

            return (T)(object)ConvertArrayDynamic(array, typeof(T));
        }

        return (T)ConvertFieldValue(value, typeof(T));
    }

    private static TElement[] ConvertOneDimensionalArray<TElement>(Array value)
    {
        if (value.Rank != 1 || value.GetLowerBound(0) != 0)
        {
            throw new NotSupportedException(
                $"The statically rooted {typeof(TElement).Name}[] conversion requires a " +
                "one-dimensional array with the standard PostgreSQL lower bound of 1.");
        }

        var result = new TElement[value.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var item = value.GetValue(index) ??
                throw new InvalidCastException(
                    $"A database NULL cannot be read as {typeof(TElement).FullName}.");
            result[index] = ConvertFieldValue<TElement>(item);
        }

        return result;
    }

    [RequiresDynamicCode(
        "Converting to an array type selected at runtime requires dynamic code. " +
        "NativeAOT supports codec-native arrays and common one-dimensional conversions.")]
    private static Array ConvertArrayDynamic(Array value, Type targetType)
    {
        if (targetType.GetArrayRank() != value.Rank)
        {
            throw new InvalidCastException(
                $"A rank-{value.Rank} array cannot be read as {targetType.FullName}.");
        }

        var elementType = targetType.GetElementType()!;
        var lengths = Enumerable.Range(0, value.Rank).Select(value.GetLength).ToArray();
        var lowerBounds = Enumerable.Range(0, value.Rank).Select(value.GetLowerBound).ToArray();
        var result = Array.CreateInstance(elementType, lengths, lowerBounds);
        var indexes = (int[])lowerBounds.Clone();
        foreach (var item in value)
        {
            result.SetValue(
                item is null ? null : ConvertFieldValue(item, elementType),
                indexes);
            MoveNext(value, indexes);
        }

        return result;
    }

    private static object ConvertFieldValue(object value, Type targetType)
    {
        var nonNullableTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (nonNullableTargetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is BlueTuskNumeric numeric && nonNullableTargetType == typeof(decimal))
        {
            return numeric.ToDecimal();
        }

        if (value is TimeSpan time
            && nonNullableTargetType == typeof(TimeOnly)
            && time < TimeSpan.FromDays(1))
        {
            return TimeOnly.FromTimeSpan(time);
        }

        if (value is BlueTuskInterval { IsFinite: true, Months: 0 } interval
            && nonNullableTargetType == typeof(TimeSpan))
        {
            var ticks = checked(
                (interval.Days * TimeSpan.TicksPerDay)
                + (interval.Microseconds * 10));
            return TimeSpan.FromTicks(ticks);
        }

        return Convert.ChangeType(value, nonNullableTargetType, CultureInfo.InvariantCulture);
    }

    private static void MoveNext(Array value, int[] indexes)
    {
        for (var dimension = value.Rank - 1; dimension >= 0; dimension--)
        {
            if (indexes[dimension] < value.GetUpperBound(dimension))
            {
                indexes[dimension]++;
                return;
            }

            indexes[dimension] = value.GetLowerBound(dimension);
        }
    }

    public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_portal is null)
        {
            return GetFieldValue<T>(ordinal);
        }

        var field = GetField(ordinal);
        var raw = await GetStreamingRow().ReadFieldAsync(
            ordinal,
            cancellationToken).ConfigureAwait(false);
        var value = BlueTuskValueDecoder.Decode(_types, field, raw);
        return ConvertFieldValue<T>(value);
    }

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override short GetInt16(int ordinal)
    {
        Span<byte> buffer = stackalloc byte[sizeof(short)];
        return TryReadStreamingBinaryScalar(ordinal, 21, buffer)
            ? BinaryPrimitives.ReadInt16BigEndian(buffer)
            : GetFieldValue<short>(ordinal);
    }

    public override int GetInt32(int ordinal)
    {
        if (_streamingFields is { } fields)
        {
            if ((uint)ordinal >= (uint)fields.Length)
            {
                throw new IndexOutOfRangeException(
                    $"Column ordinal {ordinal} is outside the current result.");
            }

            if (fields[ordinal] is { TypeOid: 23, FormatCode: 1 } &&
                GetStreamingRow().TryReadInt32(ordinal, out var value))
            {
                return value;
            }
        }

        Span<byte> buffer = stackalloc byte[sizeof(int)];
        return TryReadStreamingBinaryScalar(ordinal, 23, buffer)
            ? BinaryPrimitives.ReadInt32BigEndian(buffer)
            : GetFieldValue<int>(ordinal);
    }

    public override long GetInt64(int ordinal)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        return TryReadStreamingBinaryScalar(ordinal, 20, buffer)
            ? BinaryPrimitives.ReadInt64BigEndian(buffer)
            : GetFieldValue<long>(ordinal);
    }

    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    public override char GetChar(int ordinal)
    {
        var value = GetString(ordinal);
        return value.Length == 1
            ? value[0]
            : throw new InvalidCastException("The field does not contain exactly one character.");
    }

    public override DateTime GetDateTime(int ordinal) => GetValue(ordinal) switch
    {
        DateTime value => value,
        DateTimeOffset value => value.UtcDateTime,
        DateOnly value => value.ToDateTime(TimeOnly.MinValue),
        _ => throw new InvalidCastException("The field is not a PostgreSQL date or timestamp."),
    };

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (_portal is not null && GetField(ordinal) is { TypeOid: 17, FormatCode: 1 })
        {
            var row = GetStreamingRow();
            if (buffer is null)
            {
                return row.GetFieldLength(ordinal);
            }

            ValidateBufferArguments(buffer, bufferOffset, length);
            return row.ReadBytes(ordinal, dataOffset, buffer.AsSpan(bufferOffset, length));
        }

        var value = GetFieldValue<byte[]>(ordinal);
        return Copy(value, dataOffset, buffer, bufferOffset, length);
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal).ToCharArray();
        return Copy(value, dataOffset, buffer, bufferOffset, length);
    }

    public override Stream GetStream(int ordinal)
    {
        var field = GetField(ordinal);
        if (_portal is not null && field is { TypeOid: 17, FormatCode: 1 })
        {
            return GetStreamingRow().OpenFieldStream(ordinal);
        }

        return new MemoryStream(GetFieldValue<byte[]>(ordinal), writable: false);
    }

    public override TextReader GetTextReader(int ordinal)
    {
        var field = GetField(ordinal);
        if (_portal is null || !IsStreamingTextType(field.TypeOid))
        {
            return new StringReader(GetString(ordinal));
        }

        var stream = GetStreamingRow().OpenFieldStream(ordinal);
        if (field is { TypeOid: 3802, FormatCode: 1 })
        {
            var version = stream.ReadByte();
            if (version != 1)
            {
                stream.Dispose();
                throw new InvalidDataException($"PostgreSQL jsonb binary version {version} is not supported.");
            }
        }

        return new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: false);
    }

    public override DataTable? GetSchemaTable() => null;

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _portal?.Dispose();
        }
        finally
        {
            try
            {
                CompleteReaderLifetime();
            }
            finally
            {
                if (_executionConnection is { HasOpenSession: false, State: ConnectionState.Open })
                {
                    _executionConnection.Close();
                }

                var connection = Interlocked.Exchange(ref _connectionToClose, null);
                connection?.Close();
            }
        }
    }

    public override async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            if (_portal is not null)
            {
                await _portal.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await CompleteReaderLifetimeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_executionConnection is { HasOpenSession: false, State: ConnectionState.Open })
                {
                    await _executionConnection.CloseAsync().ConfigureAwait(false);
                }

                var connection = Interlocked.Exchange(ref _connectionToClose, null);
                if (connection is not null)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private BlueTuskFieldDescription GetField(int ordinal)
    {
        var fields = CurrentFields;
        if (fields.Count == 0)
        {
            throw new InvalidOperationException("The reader has no current result.");
        }

        return fields[ValidateOrdinal(ordinal)];
    }

    private int ValidateOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
        {
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the current result.");
        }

        return ordinal;
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The data reader is closed.");
        }
    }

    private int GetRecordsAffected()
    {
        var total = 0;
        var found = false;
        if (_portal is not null)
        {
            return _portal.CommandTag is { } commandTag &&
                   BlueTuskCommandTagParser.TryGetRecordsAffected(commandTag, out var streamedCount)
                ? streamedCount
                : -1;
        }

        foreach (var resultSet in _result!.ResultSets)
        {
            if (BlueTuskCommandTagParser.TryGetRecordsAffected(resultSet.CommandTag, out var count))
            {
                total = checked(total + count);
                found = true;
            }
        }

        return found ? total : -1;
    }

    private static long Copy<T>(T[] source, long dataOffset, T[]? destination, int destinationOffset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataOffset, source.Length);

        if (destination is null)
        {
            return source.Length;
        }

        var count = Math.Min(length, source.Length - checked((int)dataOffset));
        source.AsSpan(checked((int)dataOffset), count).CopyTo(destination.AsSpan(destinationOffset));
        return count;
    }

    private bool ReadStreaming()
    {
        if (_singleRow && _streamingRowsReturned != 0)
        {
            _portal!.Dispose();
            _streamingRow = null;
            return false;
        }

        if (_prefetchedStreamingRow is not null)
        {
            _streamingRow = _prefetchedStreamingRow;
            _prefetchedStreamingRow = null;
        }
        else
        {
            _streamingRow = ReadPortalRow();
        }

        if (_streamingRow is null)
        {
            return false;
        }

        _streamingRowsReturned++;
        return true;
    }

    private BlueTuskPortalRow? PrefetchStreamingRow()
    {
        EnsureOpen();
        if (_prefetchedStreamingRow is null && _streamingRowsReturned == 0)
        {
            _prefetchedStreamingRow = ReadPortalRow();
        }

        return _prefetchedStreamingRow;
    }

    private BlueTuskPortalRow? ReadPortalRow()
    {
        try
        {
            return _portal!.Read();
        }
        catch (BlueTuskServerException exception)
        {
            throw TranslateServerException(exception);
        }
        catch (Exception) when (_executionConnection is { HasOpenSession: false })
        {
            _executionConnection.Close();
            throw;
        }
    }

    private BlueTuskPortalRow GetStreamingRow() =>
        _streamingRow ?? throw new InvalidOperationException("The reader is not positioned on a row.");

    private bool TryReadStreamingBinaryScalar(
        int ordinal,
        uint expectedTypeOid,
        Span<byte> destination) =>
        _portal is not null &&
        GetField(ordinal) is { FormatCode: 1 } field &&
        field.TypeOid == expectedTypeOid &&
        GetStreamingRow().ReadFieldExactly(ordinal, destination);

    private Exception TranslateServerException(BlueTuskServerException exception) =>
        _streamingCommand?.TranslateReaderServerException(exception) ?? new BlueTuskException(exception);

    private void CompleteReaderLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeCompleted, 1) == 0)
        {
            _streamingCommand?.CompleteStreamingExecution(_streamingTimeoutTimer);
        }
    }

    private ValueTask CompleteReaderLifetimeAsync()
    {
        if (Interlocked.Exchange(ref _lifetimeCompleted, 1) == 0)
        {
            _streamingCommand?.CompleteStreamingExecution(_streamingTimeoutTimer);
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsStreamingTextType(uint oid) =>
        oid is 18 or 19 or 25 or 114 or 142 or 1042 or 1043 or 3802;

    private static void ValidateBufferArguments(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer offset and length exceed the destination array.");
        }
    }
}
