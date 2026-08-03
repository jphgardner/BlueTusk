namespace BlueTusk.EntityFrameworkCore.ExclusionConstraints;

/// <summary>PostgreSQL null ordering for an exclusion-constraint element.</summary>
public enum BlueTuskExclusionNullSortOrder
{
    /// <summary>Uses PostgreSQL's default for the element's sort direction.</summary>
    Default,

    /// <summary>Places null values before non-null values.</summary>
    NullsFirst,

    /// <summary>Places null values after non-null values.</summary>
    NullsLast,
}

/// <summary>A validated operator-class or index storage parameter.</summary>
public sealed record BlueTuskExclusionParameterDefinition(string Name, string Value);

/// <summary>One indexed expression and comparison operator in an exclusion constraint.</summary>
public sealed record BlueTuskExclusionElementDefinition(
    string Expression,
    bool IsColumn,
    bool IsPreformatted,
    string Operator,
    string? OperatorSchema,
    string? Collation,
    string? CollationSchema,
    string? OperatorClass,
    string? OperatorClassSchema,
    IReadOnlyList<BlueTuskExclusionParameterDefinition> OperatorClassParameters,
    bool Descending,
    BlueTuskExclusionNullSortOrder NullSortOrder);

/// <summary>A PostgreSQL table exclusion constraint and its backing-index options.</summary>
public sealed record BlueTuskExclusionConstraintDefinition(
    string Name,
    string IndexMethod,
    IReadOnlyList<BlueTuskExclusionElementDefinition> Elements,
    IReadOnlyList<string> IncludedColumns,
    IReadOnlyList<BlueTuskExclusionParameterDefinition> StorageParameters,
    string? Tablespace,
    string? PredicateSql,
    bool IsDeferrable,
    bool IsInitiallyDeferred);

/// <summary>Provider-owned exclusion constraints attached to one PostgreSQL table.</summary>
public sealed record BlueTuskExclusionConstraintTableDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<BlueTuskExclusionConstraintDefinition> Constraints);
