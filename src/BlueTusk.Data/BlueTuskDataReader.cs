using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    private readonly BlueTuskQueryResult _result;
    private BlueTuskConnection? _connectionToClose;
    private int _resultIndex;
    private int _rowIndex = -1;
    private bool _closed;

    internal BlueTuskDataReader(BlueTuskQueryResult result, BlueTuskConnection? connectionToClose)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _connectionToClose = connectionToClose;
    }

    public override int FieldCount => CurrentResultSet?.Fields.Count ?? 0;

    public override bool HasRows => CurrentResultSet?.Rows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => GetRecordsAffected();

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    private BlueTuskResultSet? CurrentResultSet =>
        _resultIndex < _result.ResultSets.Count ? _result.ResultSets[_resultIndex] : null;

    private BlueTuskDataRow CurrentRow =>
        CurrentResultSet is { } resultSet && _rowIndex >= 0 && _rowIndex < resultSet.Rows.Count
            ? resultSet.Rows[_rowIndex]
            : throw new InvalidOperationException("The reader is not positioned on a row.");

    public override bool Read()
    {
        EnsureOpen();
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
        return Task.FromResult(Read());
    }

    public override bool NextResult()
    {
        EnsureOpen();
        if (_resultIndex + 1 >= _result.ResultSets.Count)
        {
            return false;
        }

        _resultIndex++;
        _rowIndex = -1;
        return true;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    public override string GetName(int ordinal) => GetField(ordinal).Name;

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var fields = CurrentResultSet?.Fields ?? throw new InvalidOperationException("The reader has no current result.");
        for (var index = 0; index < fields.Count; index++)
        {
            if (string.Equals(fields[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new IndexOutOfRangeException($"Column '{name}' was not found.");
    }

    public override string GetDataTypeName(int ordinal) => BlueTuskValueDecoder.GetDataTypeName(GetField(ordinal).TypeOid);

    public override Type GetFieldType(int ordinal) => BlueTuskValueDecoder.GetFieldType(GetField(ordinal));

    public override object GetValue(int ordinal) =>
        BlueTuskValueDecoder.Decode(GetField(ordinal), CurrentRow.Values[ordinal]);

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

    public override bool IsDBNull(int ordinal) => CurrentRow.Values[ValidateOrdinal(ordinal)] is null;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsDBNull(ordinal));
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DBNull)
        {
            throw new InvalidCastException("A database NULL cannot be read as a non-null value.");
        }

        if (value is BlueTuskNumeric numeric && typeof(T) == typeof(decimal))
        {
            return (T)(object)numeric.ToDecimal();
        }

        if (value is TimeSpan time && typeof(T) == typeof(TimeOnly) && time < TimeSpan.FromDays(1))
        {
            return (T)(object)TimeOnly.FromTimeSpan(time);
        }

        return value is T typed
            ? typed
            : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFieldValue<T>(ordinal));
    }

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

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
        var value = GetFieldValue<byte[]>(ordinal);
        return Copy(value, dataOffset, buffer, bufferOffset, length);
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal).ToCharArray();
        return Copy(value, dataOffset, buffer, bufferOffset, length);
    }

    public override Stream GetStream(int ordinal) => new MemoryStream(GetFieldValue<byte[]>(ordinal), writable: false);

    public override TextReader GetTextReader(int ordinal) => new StringReader(GetString(ordinal));

    public override DataTable? GetSchemaTable() => null;

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var connection = Interlocked.Exchange(ref _connectionToClose, null);
        connection?.Close();
    }

    public override async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var connection = Interlocked.Exchange(ref _connectionToClose, null);
        if (connection is not null)
        {
            await connection.CloseAsync().ConfigureAwait(false);
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
        var resultSet = CurrentResultSet ?? throw new InvalidOperationException("The reader has no current result.");
        return resultSet.Fields[ValidateOrdinal(ordinal)];
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
        foreach (var resultSet in _result.ResultSets)
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
}
