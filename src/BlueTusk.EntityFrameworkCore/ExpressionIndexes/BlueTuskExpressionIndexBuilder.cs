using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;

namespace BlueTusk.EntityFrameworkCore.ExpressionIndexes;

/// <summary>Builds a PostgreSQL expression or mixed-key index.</summary>
public sealed class BlueTuskExpressionIndexBuilder(string name)
{
    private readonly List<string> _keySql = [];
    private readonly List<string> _includedColumns = [];
    private readonly Dictionary<string, string> _storageParameters = new(StringComparer.Ordinal);
    private string _method = "btree";
    private bool _isUnique;
    private bool? _nullsDistinct;
    private string? _predicateSql;
    private string? _tablespace;
    private bool _isConcurrent;

    /// <summary>Adds trusted, preformatted PostgreSQL SQL for one or more ordered index keys.</summary>
    public BlueTuskExpressionIndexBuilder HasKeySql(params string[] keySql)
    {
        ArgumentNullException.ThrowIfNull(keySql);
        foreach (var key in keySql)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            _keySql.Add(key);
        }

        return this;
    }

    /// <summary>Configures the PostgreSQL index access method.</summary>
    public BlueTuskExpressionIndexBuilder UseMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        _method = method;
        return this;
    }

    /// <summary>Adds quoted database-column names to the INCLUDE list.</summary>
    public BlueTuskExpressionIndexBuilder IncludeColumns(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column);
            _includedColumns.Add(column);
        }

        return this;
    }

    /// <summary>Configures the index as unique or non-unique.</summary>
    public BlueTuskExpressionIndexBuilder IsUnique(bool unique = true)
    {
        _isUnique = unique;
        return this;
    }

    /// <summary>Controls whether a unique index treats null values as distinct.</summary>
    public BlueTuskExpressionIndexBuilder HasNullsDistinct(bool distinct = true)
    {
        _nullsDistinct = distinct;
        return this;
    }

    /// <summary>Adds a trusted PostgreSQL partial-index predicate.</summary>
    public BlueTuskExpressionIndexBuilder HasFilter(string? predicateSql)
    {
        if (predicateSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(predicateSql);
        }

        _predicateSql = predicateSql;
        return this;
    }

    /// <summary>Adds or replaces a validated index storage parameter.</summary>
    public BlueTuskExpressionIndexBuilder HasStorageParameter(string name, string value)
    {
        BlueTuskExpressionIndexMetadata.ValidateStorageParameter(name, value);
        _storageParameters[name] = value;
        return this;
    }

    /// <summary>Places the index in a PostgreSQL tablespace.</summary>
    public BlueTuskExpressionIndexBuilder UseTablespace(string? tablespace)
    {
        if (tablespace is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tablespace);
        }

        _tablespace = tablespace;
        return this;
    }

    /// <summary>Creates and drops the index with PostgreSQL's CONCURRENTLY option.</summary>
    public BlueTuskExpressionIndexBuilder IsConcurrent(bool concurrent = true)
    {
        _isConcurrent = concurrent;
        return this;
    }

    internal BlueTuskExpressionIndexDefinition Build() =>
        BlueTuskExpressionIndexMetadata.Normalize(new BlueTuskExpressionIndexDefinition(
            name,
            _method,
            _keySql,
            _includedColumns,
            _storageParameters.Select(parameter =>
                    new BlueTuskExpressionIndexStorageParameterDefinition(parameter.Key, parameter.Value))
                .ToArray(),
            _isUnique,
            _nullsDistinct,
            _predicateSql,
            _tablespace,
            _isConcurrent));
}
