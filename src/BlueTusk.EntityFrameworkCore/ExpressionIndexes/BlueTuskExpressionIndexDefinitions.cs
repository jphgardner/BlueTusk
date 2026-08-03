namespace BlueTusk.EntityFrameworkCore.ExpressionIndexes;

/// <summary>A validated PostgreSQL index storage parameter.</summary>
public sealed record BlueTuskExpressionIndexStorageParameterDefinition(string Name, string Value);

/// <summary>
/// A PostgreSQL expression or mixed-key index represented with trusted, preformatted key SQL.
/// </summary>
public sealed record BlueTuskExpressionIndexDefinition(
    string Name,
    string Method,
    IReadOnlyList<string> KeySql,
    IReadOnlyList<string> IncludedColumns,
    IReadOnlyList<BlueTuskExpressionIndexStorageParameterDefinition> StorageParameters,
    bool IsUnique,
    bool? NullsDistinct,
    string? PredicateSql,
    string? Tablespace,
    bool IsConcurrent);

/// <summary>Provider-owned expression indexes attached to one PostgreSQL table.</summary>
public sealed record BlueTuskExpressionIndexTableDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<BlueTuskExpressionIndexDefinition> Indexes);
