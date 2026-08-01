using System.Globalization;
using System.Text.Json.Serialization;

namespace BlueTusk.EntityFrameworkCore.Partitioning;

/// <summary>PostgreSQL declarative table-partitioning strategies.</summary>
public enum BlueTuskPartitionStrategy
{
    /// <summary>Range partitioning.</summary>
    Range,

    /// <summary>List partitioning.</summary>
    List,

    /// <summary>Hash partitioning.</summary>
    Hash,
}

/// <summary>The kind of a PostgreSQL partition bound.</summary>
public enum BlueTuskPartitionBoundKind
{
    /// <summary>A range bound.</summary>
    Range,

    /// <summary>A list bound.</summary>
    List,

    /// <summary>A hash modulus/remainder bound.</summary>
    Hash,

    /// <summary>The default partition.</summary>
    Default,

    /// <summary>A trusted PostgreSQL bound clause retained verbatim.</summary>
    Sql,
}

/// <summary>A PostgreSQL partition-key column or trusted SQL expression.</summary>
public sealed record BlueTuskPartitionKeyDefinition(
    string Expression,
    bool IsColumn,
    string? Collation = null,
    string? OperatorClass = null)
{
    /// <summary>Creates a column partition key.</summary>
    public static BlueTuskPartitionKeyDefinition Column(
        string column,
        string? collation = null,
        string? operatorClass = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new BlueTuskPartitionKeyDefinition(column, true, collation, operatorClass);
    }

    /// <summary>
    /// Creates a trusted SQL-expression partition key. The SQL must be fixed application metadata.
    /// </summary>
    public static BlueTuskPartitionKeyDefinition SqlExpression(
        string sql,
        string? collation = null,
        string? operatorClass = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return new BlueTuskPartitionKeyDefinition(sql, false, collation, operatorClass);
    }
}

/// <summary>A constant PostgreSQL partition-bound value.</summary>
public readonly record struct BlueTuskPartitionValue
{
    private BlueTuskPartitionValue(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Sql = sql;
    }

    /// <summary>Gets the SQL representation of the constant.</summary>
    public string Sql { get; }

    /// <summary>PostgreSQL's range <c>MINVALUE</c> marker.</summary>
    public static BlueTuskPartitionValue MinValue { get; } = new("MINVALUE");

    /// <summary>PostgreSQL's range <c>MAXVALUE</c> marker.</summary>
    public static BlueTuskPartitionValue MaxValue { get; } = new("MAXVALUE");

    /// <summary>A PostgreSQL <c>NULL</c> list value.</summary>
    public static BlueTuskPartitionValue Null { get; } = new("NULL");

    /// <summary>Creates a quoted PostgreSQL string literal.</summary>
    public static BlueTuskPartitionValue Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new BlueTuskPartitionValue($"'{value.Replace("'", "''", StringComparison.Ordinal)}'");
    }

    /// <summary>Creates a PostgreSQL Boolean literal.</summary>
    public static BlueTuskPartitionValue Literal(bool value) => new(value ? "TRUE" : "FALSE");

    /// <summary>Creates a PostgreSQL integer literal.</summary>
    public static BlueTuskPartitionValue Literal(int value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a PostgreSQL bigint literal.</summary>
    public static BlueTuskPartitionValue Literal(long value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a PostgreSQL numeric literal.</summary>
    public static BlueTuskPartitionValue Literal(decimal value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a PostgreSQL date literal.</summary>
    public static BlueTuskPartitionValue Literal(DateOnly value) =>
        new($"DATE '{value:yyyy-MM-dd}'");

    /// <summary>Creates a PostgreSQL timestamp-with-time-zone literal.</summary>
    public static BlueTuskPartitionValue Literal(DateTimeOffset value) =>
        new($"TIMESTAMPTZ '{value.ToString("O", CultureInfo.InvariantCulture)}'");

    /// <summary>Creates a PostgreSQL UUID literal.</summary>
    public static BlueTuskPartitionValue Literal(Guid value) => new($"UUID '{value:D}'");

    /// <summary>
    /// Creates a trusted SQL partition value. The SQL must be fixed application metadata and never user input.
    /// </summary>
    public static BlueTuskPartitionValue FromSql(string sql) => new(sql);
}

/// <summary>A PostgreSQL partition bound.</summary>
public sealed record BlueTuskPartitionBound
{
    /// <summary>Gets the bound kind.</summary>
    [JsonInclude]
    public BlueTuskPartitionBoundKind Kind { get; private init; }

    /// <summary>Gets the inclusive range lower tuple.</summary>
    [JsonInclude]
    public string[] From { get; private init; } = [];

    /// <summary>Gets the exclusive range upper tuple.</summary>
    [JsonInclude]
    public string[] To { get; private init; } = [];

    /// <summary>Gets the list-value tuples.</summary>
    [JsonInclude]
    public string[][] Values { get; private init; } = [];

    /// <summary>Gets the hash modulus.</summary>
    [JsonInclude]
    public int Modulus { get; private init; }

    /// <summary>Gets the hash remainder.</summary>
    [JsonInclude]
    public int Remainder { get; private init; }

    /// <summary>Gets an exact trusted SQL bound clause, when retained from a catalogue or supplied explicitly.</summary>
    [JsonInclude]
    public string? Sql { get; private init; }

    /// <summary>Creates a single-key range bound.</summary>
    public static BlueTuskPartitionBound Range(
        BlueTuskPartitionValue from,
        BlueTuskPartitionValue to) => Range([from], [to]);

    /// <summary>Creates a range bound with one value per partition key.</summary>
    public static BlueTuskPartitionBound Range(
        IReadOnlyList<BlueTuskPartitionValue> from,
        IReadOnlyList<BlueTuskPartitionValue> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ValidateValues(from, nameof(from));
        ValidateValues(to, nameof(to));
        return new BlueTuskPartitionBound
        {
            Kind = BlueTuskPartitionBoundKind.Range,
            From = from.Select(value => value.Sql).ToArray(),
            To = to.Select(value => value.Sql).ToArray(),
        };
    }

    /// <summary>Creates a single-key list bound.</summary>
    public static BlueTuskPartitionBound List(params BlueTuskPartitionValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values, nameof(values));
        return new BlueTuskPartitionBound
        {
            Kind = BlueTuskPartitionBoundKind.List,
            Values = values.Select(value => new[] { value.Sql }).ToArray(),
        };
    }

    /// <summary>Creates a hash modulus/remainder bound.</summary>
    public static BlueTuskPartitionBound Hash(int modulus, int remainder)
    {
        if (modulus <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modulus), modulus, "Hash modulus must be positive.");
        }

        if (remainder < 0 || remainder >= modulus)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainder),
                remainder,
                "Hash remainder must be non-negative and less than the modulus.");
        }

        return new BlueTuskPartitionBound
        {
            Kind = BlueTuskPartitionBoundKind.Hash,
            Modulus = modulus,
            Remainder = remainder,
        };
    }

    /// <summary>Creates the default partition bound.</summary>
    public static BlueTuskPartitionBound Default() =>
        new() { Kind = BlueTuskPartitionBoundKind.Default };

    /// <summary>
    /// Creates a trusted PostgreSQL clause such as <c>FOR VALUES ...</c> or <c>DEFAULT</c>.
    /// </summary>
    public static BlueTuskPartitionBound FromSql(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return new BlueTuskPartitionBound { Kind = BlueTuskPartitionBoundKind.Sql, Sql = sql };
    }

    private static void ValidateValues(
        IReadOnlyList<BlueTuskPartitionValue> values,
        string parameterName)
    {
        if (values.Any(value => string.IsNullOrWhiteSpace(value.Sql)))
        {
            throw new ArgumentException(
                "Partition values must be created with a BlueTuskPartitionValue factory.",
                parameterName);
        }
    }
}

/// <summary>A child table in a PostgreSQL declarative partition tree.</summary>
public sealed record BlueTuskPartitionDefinition(
    string Name,
    string? Schema,
    BlueTuskPartitionBound Bound,
    BlueTuskPartitioningDefinition? Partitioning = null);

/// <summary>PostgreSQL partitioning metadata for one table.</summary>
public sealed record BlueTuskPartitioningDefinition(
    BlueTuskPartitionStrategy Strategy,
    IReadOnlyList<BlueTuskPartitionKeyDefinition> Keys,
    IReadOnlyList<BlueTuskPartitionDefinition> Partitions,
    string? KeySql = null);

/// <summary>A named PostgreSQL partitioned-table definition.</summary>
public sealed record BlueTuskPartitionedTableDefinition(
    string Name,
    string? Schema,
    BlueTuskPartitioningDefinition Partitioning);
