using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Streams;

public enum SchemaChangeMode
{
    PauseAndReload,
    Fail,
    ContinueDynamically,
    ApplicationCallback,
}

public enum TypedDecodingFailureMode
{
    Pause,
    ContinueDynamically,
    ApplicationCallback,
}

public enum ChangeMappingResolution
{
    Pause,
    Fail,
    ContinueDynamically,
}

public sealed record ChangeSchemaDifference(
    string ExpectedFingerprint,
    string ActualFingerprint,
    ChangeTable ExpectedTable,
    ChangeTable ActualTable);

public sealed record TypedChangeDecodingFailure(
    ChangeTable Table,
    ChangeColumn Column,
    Type TargetType,
    ChangeColumnValue Value,
    string Message,
    Exception? Exception = null);

public sealed record ChangeMappingPolicy
{
    public SchemaChangeMode SchemaChangeMode { get; init; } = SchemaChangeMode.PauseAndReload;

    public TypedDecodingFailureMode DecodingFailureMode { get; init; } =
        TypedDecodingFailureMode.Pause;

    public Func<ChangeSchemaDifference, ChangeMappingResolution>? SchemaChangeCallback { get; init; }

    public Func<TypedChangeDecodingFailure, ChangeMappingResolution>? DecodingFailureCallback { get; init; }
}

public sealed class ChangeSchemaReloadRequiredException : Exception
{
    public ChangeSchemaReloadRequiredException(ChangeSchemaDifference difference)
        : base(
            $"The schema for {difference.ActualTable} changed from " +
            $"{difference.ExpectedFingerprint} to {difference.ActualFingerprint}; " +
            "the stream is paused until mappings are reloaded.")
    {
        Difference = difference;
    }

    public ChangeSchemaDifference Difference { get; }
}

public sealed class ChangeSchemaMismatchException : Exception
{
    public ChangeSchemaMismatchException(ChangeSchemaDifference difference)
        : base(
            $"The schema for {difference.ActualTable} does not match the configured mapping " +
            $"({difference.ExpectedFingerprint} != {difference.ActualFingerprint}).")
    {
        Difference = difference;
    }

    public ChangeSchemaDifference Difference { get; }
}

public sealed class TypedChangeDecodingException : Exception
{
    public TypedChangeDecodingException(TypedChangeDecodingFailure failure)
        : base(failure.Message, failure.Exception)
    {
        Failure = failure;
    }

    public TypedChangeDecodingFailure Failure { get; }
}

public static class ChangeSchemaFingerprint
{
    public static string Create(ChangeTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, table.Schema);
        Add(hash, table.Name);
        Add(hash, table.ReplicaIdentity.ToString(CultureInfo.InvariantCulture));
        foreach (var column in table.Columns)
        {
            Add(hash, column.Ordinal.ToString(CultureInfo.InvariantCulture));
            Add(hash, column.Name);
            Add(hash, column.TypeOid.ToString(CultureInfo.InvariantCulture));
            Add(hash, column.TypeModifier.ToString(CultureInfo.InvariantCulture));
            Add(hash, column.IsKey ? "1" : "0");
            Add(hash, column.Type?.Namespace ?? string.Empty);
            Add(hash, column.Type?.Name ?? string.Empty);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Add(IncrementalHash hash, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
        hash.AppendData(length);
        if (byteCount == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
    }
}

public delegate TProperty ChangeColumnDecoder<out TProperty>(
    ChangeColumn column,
    ChangeColumnValue value);

public sealed record ChangePropertyMapping(
    string PropertyName,
    Type PropertyType,
    string ColumnName,
    uint? ExpectedTypeOid);

public sealed class ChangeEntityMappingBuilder<T>
    where T : class, new()
{
    private readonly Dictionary<string, PropertyBinding<T>> _bindings =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private string? _schema;
    private string? _table;
    private bool _useConventions = true;

    public ChangeEntityMappingBuilder<T> ToTable(string schema, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        _schema = schema;
        _table = table;
        return this;
    }

    public ChangeEntityMappingBuilder<T> HasKey(params string[] columnNames)
    {
        ArgumentNullException.ThrowIfNull(columnNames);
        if (columnNames.Length == 0)
        {
            throw new ArgumentException("At least one key column is required.", nameof(columnNames));
        }

        _keys.Clear();
        foreach (var name in columnNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _keys.Add(name);
        }

        return this;
    }

    public ChangeEntityMappingBuilder<T> UseConventions(bool enabled = true)
    {
        _useConventions = enabled;
        return this;
    }

    public ChangeEntityMappingBuilder<T> Property<TProperty>(
        Expression<Func<T, TProperty>> property,
        string? columnName = null,
        uint? expectedTypeOid = null,
        ChangeColumnDecoder<TProperty>? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        var propertyInfo = GetProperty(property);
        var sourceColumn = string.IsNullOrWhiteSpace(columnName)
            ? ToSnakeCase(propertyInfo.Name)
            : columnName;
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceColumn);
        _bindings[propertyInfo.Name] = PropertyBinding<T>.Create(
            propertyInfo,
            sourceColumn,
            expectedTypeOid,
            decoder);
        return this;
    }

    public ChangeEntityMapping<T> Build(ChangeTable table, ChangeMappingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var schema = _schema ?? table.Schema;
        var tableName = _table ?? table.Name;
        if (!string.Equals(schema, table.Schema, StringComparison.Ordinal) ||
            !string.Equals(tableName, table.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The supplied relation {table} does not match configured table {schema}.{tableName}.",
                nameof(table));
        }

        if (_useConventions)
        {
            AddConventionBindings(table);
        }

        if (_bindings.Count == 0)
        {
            throw new InvalidOperationException($"No CLR properties are mapped for {table}.");
        }

        foreach (var binding in _bindings.Values)
        {
            if (!table.TryGetColumn(binding.ColumnName, out var column))
            {
                throw new InvalidOperationException(
                    $"Mapped column {table}.{binding.ColumnName} does not exist.");
            }

            if (binding.ExpectedTypeOid is { } oid && oid != column.TypeOid)
            {
                throw new InvalidOperationException(
                    $"Mapped column {table}.{binding.ColumnName} has type OID {column.TypeOid}; {oid} was expected.");
            }

            binding.ColumnOrdinal = column.Ordinal;
        }

        var keys = _keys.Count == 0
            ? GetKeyNames(table)
            : _keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var key in keys)
        {
            if (!table.TryGetColumn(key, out _))
            {
                throw new InvalidOperationException($"Mapped key column {table}.{key} does not exist.");
            }
        }

        return new ChangeEntityMapping<T>(
            table,
            _bindings.Values.OrderBy(binding => binding.ColumnName, StringComparer.Ordinal).ToArray(),
            keys,
            policy ?? new ChangeMappingPolicy());
    }

    private static string[] GetKeyNames(ChangeTable table)
    {
        var keyOrdinals = table.KeyOrdinals;
        var keys = new string[keyOrdinals.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = table.Columns[keyOrdinals[index]].Name;
        }

        return keys;
    }

    private void AddConventionBindings(ChangeTable table)
    {
        var columns = table.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (_bindings.ContainsKey(property.Name) ||
                property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            var snakeCase = ToSnakeCase(property.Name);
            if (!columns.TryGetValue(snakeCase, out var column) &&
                !columns.TryGetValue(property.Name, out column))
            {
                continue;
            }

            _bindings[property.Name] = PropertyBinding<T>.CreateDefault(property, column.Name);
        }
    }

    private static PropertyInfo GetProperty<TProperty>(Expression<Func<T, TProperty>> expression)
    {
        var member = expression.Body as MemberExpression;
        if (member?.Member is not PropertyInfo property ||
            property.DeclaringType is null ||
            !property.DeclaringType.IsAssignableFrom(typeof(T)) ||
            property.SetMethod is not { IsPublic: true })
        {
            throw new ArgumentException(
                "The expression must select a public writable property.",
                nameof(expression));
        }

        return property;
    }

    internal static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

public sealed class ChangeEntityMapping<T>
    where T : class, new()
{
    private readonly ReadOnlyCollection<PropertyBinding<T>> _bindings;
    private readonly ReadOnlyCollection<ChangePropertyMapping> _properties;
    private readonly ReadOnlyCollection<string> _keyColumns;
    private readonly ChangeMappingPolicy _policy;

    internal ChangeEntityMapping(
        ChangeTable table,
        PropertyBinding<T>[] bindings,
        string[] keyColumns,
        ChangeMappingPolicy policy)
    {
        Table = table;
        SchemaFingerprint = ChangeSchemaFingerprint.Create(table);
        _bindings = Array.AsReadOnly(bindings);
        _properties = Array.AsReadOnly(bindings.Select(binding => binding.Metadata).ToArray());
        _keyColumns = Array.AsReadOnly(keyColumns);
        _policy = policy;
        MappingFingerprint = CreateMappingFingerprint(table, bindings, keyColumns);
    }

    public ChangeTable Table { get; }

    public IReadOnlyList<ChangePropertyMapping> Properties => _properties;

    public IReadOnlyList<string> KeyColumns => _keyColumns;

    public string SchemaFingerprint { get; }

    public string MappingFingerprint { get; }

    public ChangeRow<T> MapRow(ChangeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        EnsureTable(row.Table);
        var value = new T();
        foreach (var binding in _bindings)
        {
            var column = row.Table.Columns[binding.ColumnOrdinal];
            var source = row[column.Ordinal];
            if (source.State is ChangeColumnState.NotPublished or
                ChangeColumnState.OldValueUnavailable or
                ChangeColumnState.UnchangedToast)
            {
                return new ChangeRow<T>(row, default, false);
            }

            try
            {
                binding.Set(value, column, source);
            }
            catch (Exception exception) when (exception is not TypedChangeDecodingException)
            {
                var message = source.State == ChangeColumnState.DecodingFailure
                    ? source.DecodingError ?? $"PostgreSQL value for {row.Table}.{column.Name} could not be decoded."
                    : $"PostgreSQL value for {row.Table}.{column.Name} could not be decoded as {binding.PropertyType.FullName}.";
                throw new TypedChangeDecodingException(
                    new TypedChangeDecodingFailure(
                        row.Table,
                        column,
                        binding.PropertyType,
                        source,
                        message,
                        exception));
            }
        }

        return new ChangeRow<T>(row, value, true);
    }

    public Change Map(Change change)
    {
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            return change switch
            {
                InsertChange insert when IsMappedTable(insert.NewRow.Table) =>
                    new InsertChange<T>(insert.Id, MapRow(insert.NewRow)),
                UpdateChange update when IsMappedTable(update.NewRow.Table) =>
                    new UpdateChange<T>(
                        update.Id,
                        MapRow(update.OldRow),
                        MapRow(update.NewRow),
                        update.ChangedColumns),
                DeleteChange delete when IsMappedTable(delete.OldRow.Table) =>
                    new DeleteChange<T>(delete.Id, MapRow(delete.OldRow)),
                TruncateChange truncate when truncate.Tables.Any(IsMappedTable) =>
                    new TruncateChange<T>(
                        truncate.Id,
                        truncate.Tables,
                        truncate.Cascade,
                        truncate.RestartIdentity),
                _ => change,
            };
        }
        catch (ChangeSchemaReloadRequiredException)
        {
            throw;
        }
        catch (ChangeSchemaMismatchException)
        {
            throw;
        }
        catch (DynamicChangeContinuationException)
        {
            return change;
        }
        catch (TypedChangeDecodingException exception)
        {
            if (ResolveDecodingFailure(exception.Failure) == ChangeMappingResolution.ContinueDynamically)
            {
                return change;
            }

            throw;
        }
    }

    private void EnsureTable(ChangeTable actual)
    {
        if (!IsMappedTable(actual))
        {
            throw new ArgumentException(
                $"Relation {actual} does not match mapping {Table}.",
                nameof(actual));
        }

        var fingerprint = ChangeSchemaFingerprint.Create(actual);
        if (string.Equals(SchemaFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        var difference = new ChangeSchemaDifference(SchemaFingerprint, fingerprint, Table, actual);
        var resolution = _policy.SchemaChangeMode switch
        {
            SchemaChangeMode.Fail => ChangeMappingResolution.Fail,
            SchemaChangeMode.ContinueDynamically => ChangeMappingResolution.ContinueDynamically,
            SchemaChangeMode.ApplicationCallback =>
                _policy.SchemaChangeCallback?.Invoke(difference) ?? ChangeMappingResolution.Pause,
            _ => ChangeMappingResolution.Pause,
        };

        switch (resolution)
        {
            case ChangeMappingResolution.Fail:
                throw new ChangeSchemaMismatchException(difference);
            case ChangeMappingResolution.ContinueDynamically:
                throw new DynamicChangeContinuationException(difference);
            default:
                throw new ChangeSchemaReloadRequiredException(difference);
        }
    }

    private ChangeMappingResolution ResolveDecodingFailure(TypedChangeDecodingFailure failure) =>
        _policy.DecodingFailureMode switch
        {
            TypedDecodingFailureMode.ContinueDynamically => ChangeMappingResolution.ContinueDynamically,
            TypedDecodingFailureMode.ApplicationCallback =>
                _policy.DecodingFailureCallback?.Invoke(failure) ?? ChangeMappingResolution.Pause,
            _ => ChangeMappingResolution.Pause,
        };

    private bool IsMappedTable(ChangeTable table) =>
        string.Equals(Table.Schema, table.Schema, StringComparison.Ordinal) &&
        string.Equals(Table.Name, table.Name, StringComparison.Ordinal);

    private static string CreateMappingFingerprint(
        ChangeTable table,
        IEnumerable<PropertyBinding<T>> bindings,
        IEnumerable<string> keys)
    {
        var canonical = new StringBuilder()
            .Append(typeof(T).AssemblyQualifiedName).Append('\n')
            .Append(table.Schema).Append('\n')
            .Append(table.Name).Append('\n');
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            canonical.Append("key:").Append(key).Append('\n');
        }

        foreach (var binding in bindings.OrderBy(item => item.ColumnName, StringComparer.Ordinal))
        {
            canonical
                .Append(binding.PropertyName).Append('|')
                .Append(binding.PropertyType.AssemblyQualifiedName).Append('|')
                .Append(binding.ColumnName).Append('|')
                .Append(binding.ExpectedTypeOid?.ToString(CultureInfo.InvariantCulture) ?? "*")
                .Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private sealed class DynamicChangeContinuationException : Exception
    {
        public DynamicChangeContinuationException(ChangeSchemaDifference difference)
            : base($"Continue {difference.ActualTable} dynamically.")
        {
        }
    }
}

internal abstract class PropertyBinding<T>
    where T : class, new()
{
    protected PropertyBinding(PropertyInfo property, string columnName, uint? expectedTypeOid)
    {
        PropertyName = property.Name;
        PropertyType = property.PropertyType;
        ColumnName = columnName;
        ExpectedTypeOid = expectedTypeOid;
        Metadata = new ChangePropertyMapping(PropertyName, PropertyType, ColumnName, ExpectedTypeOid);
    }

    public string PropertyName { get; }

    public Type PropertyType { get; }

    public string ColumnName { get; }

    public uint? ExpectedTypeOid { get; }

    public int ColumnOrdinal { get; set; }

    public ChangePropertyMapping Metadata { get; }

    public abstract void Set(T target, ChangeColumn column, ChangeColumnValue value);

    public static PropertyBinding<T> Create<TProperty>(
        PropertyInfo property,
        string columnName,
        uint? expectedTypeOid,
        ChangeColumnDecoder<TProperty>? decoder) =>
        new PropertyBinding<T, TProperty>(property, columnName, expectedTypeOid, decoder);

    public static PropertyBinding<T> CreateDefault(PropertyInfo property, string columnName)
    {
        var bindingType = typeof(PropertyBinding<,>).MakeGenericType(typeof(T), property.PropertyType);
        return (PropertyBinding<T>)Activator.CreateInstance(
            bindingType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [property, columnName, null, null],
            culture: null)!;
    }
}

internal sealed class PropertyBinding<T, TProperty> : PropertyBinding<T>
    where T : class, new()
{
    private readonly Action<T, TProperty> _setter;
    private readonly ChangeColumnDecoder<TProperty> _decoder;

    public PropertyBinding(
        PropertyInfo property,
        string columnName,
        uint? expectedTypeOid,
        ChangeColumnDecoder<TProperty>? decoder)
        : base(property, columnName, expectedTypeOid)
    {
        var target = Expression.Parameter(typeof(T), "target");
        var value = Expression.Parameter(typeof(TProperty), "value");
        _setter = Expression.Lambda<Action<T, TProperty>>(
            Expression.Assign(Expression.Property(target, property), value),
            target,
            value).Compile();
        _decoder = decoder ?? ChangeValueDecoders.Decode<TProperty>;
    }

    public override void Set(T target, ChangeColumn column, ChangeColumnValue value) =>
        _setter(target, _decoder(column, value));
}

public static class ChangeValueDecoders
{
    public static T Decode<T>(ChangeColumn column, ChangeColumnValue value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        if (value.State == ChangeColumnState.DatabaseNull)
        {
            if (default(T) is not null)
            {
                throw new InvalidOperationException(
                    $"Database null cannot be assigned to non-nullable {typeof(T).FullName}.");
            }

            return default!;
        }

        if (value.State != ChangeColumnState.Value)
        {
            throw new InvalidOperationException(
                $"Column state {value.State} does not contain a decodable value.");
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object decoded = value.Encoding switch
        {
            ChangeValueEncoding.Text => DecodeText(target, value.Data.Span),
            ChangeValueEncoding.Binary => DecodeBinary(target, value.Data.Span),
            _ => throw new InvalidOperationException("A value must declare text or binary encoding."),
        };
        return (T)decoded;
    }

    private static object DecodeText(Type target, ReadOnlySpan<byte> data)
    {
        if (target == typeof(string))
        {
            return Encoding.UTF8.GetString(data);
        }

        if (target == typeof(byte[]))
        {
            return data.ToArray();
        }

        if (target == typeof(bool) && data.Length == 1)
        {
            return data[0] switch
            {
                (byte)'t' => true,
                (byte)'f' => false,
                _ => throw new FormatException("PostgreSQL boolean text must be t or f."),
            };
        }

        if (target == typeof(short) && Utf8Parser.TryParse(data, out short int16, out var consumed16) && consumed16 == data.Length)
        {
            return int16;
        }

        if (target == typeof(int) && Utf8Parser.TryParse(data, out int int32, out var consumed32) && consumed32 == data.Length)
        {
            return int32;
        }

        if (target == typeof(long) && Utf8Parser.TryParse(data, out long int64, out var consumed64) && consumed64 == data.Length)
        {
            return int64;
        }

        if (target == typeof(float) && Utf8Parser.TryParse(data, out float single, out var consumedSingle) && consumedSingle == data.Length)
        {
            return single;
        }

        if (target == typeof(double) && Utf8Parser.TryParse(data, out double doubleValue, out var consumedDouble) && consumedDouble == data.Length)
        {
            return doubleValue;
        }

        if (target == typeof(decimal) && Utf8Parser.TryParse(data, out decimal decimalValue, out var consumedDecimal) && consumedDecimal == data.Length)
        {
            return decimalValue;
        }

        var text = Encoding.UTF8.GetString(data);
        if (target == typeof(Guid))
        {
            return Guid.Parse(text, CultureInfo.InvariantCulture);
        }

        if (target == typeof(DateTime))
        {
            return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (target == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (target.IsEnum)
        {
            return Enum.Parse(target, text, ignoreCase: false);
        }

        throw new NotSupportedException(
            $"No default text change decoder is registered for {target.FullName}.");
    }

    private static object DecodeBinary(Type target, ReadOnlySpan<byte> data)
    {
        if (target == typeof(byte[]))
        {
            return data.ToArray();
        }

        if (target == typeof(bool) && data.Length == 1)
        {
            return data[0] != 0;
        }

        if (target == typeof(short) && data.Length == sizeof(short))
        {
            return BinaryPrimitives.ReadInt16BigEndian(data);
        }

        if (target == typeof(int) && data.Length == sizeof(int))
        {
            return BinaryPrimitives.ReadInt32BigEndian(data);
        }

        if (target == typeof(long) && data.Length == sizeof(long))
        {
            return BinaryPrimitives.ReadInt64BigEndian(data);
        }

        if (target == typeof(float) && data.Length == sizeof(float))
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data));
        }

        if (target == typeof(double) && data.Length == sizeof(double))
        {
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data));
        }

        if (target == typeof(Guid) && data.Length == 16)
        {
            return new Guid(data, bigEndian: true);
        }

        throw new NotSupportedException(
            $"No default binary change decoder is registered for {target.FullName} with {data.Length} bytes.");
    }
}
