using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal static class BlueTuskQueryAnnotationNames
{
    public const string DistinctOn = "BlueTusk:DistinctOn";

    public const string CommonTableExpression = "BlueTusk:CommonTableExpression";

    public const string RowLocking = "BlueTusk:RowLocking";

    public const string RecursiveCommonTableExpression = "BlueTusk:RecursiveCommonTableExpression";

    public const string TableSample = "BlueTusk:TableSample";
}

internal enum BlueTuskRowLockingStrength
{
    Update,
    NoKeyUpdate,
    Share,
    KeyShare,
}

internal enum BlueTuskTableSampleMethod
{
    System,
    Bernoulli,
}

internal enum BlueTuskCteMaterialization
{
    Default,
    Materialized,
    NotMaterialized,
}

internal sealed record BlueTuskCteClause(
    string Name,
    BlueTuskCteMaterialization Materialization);

internal sealed record BlueTuskRecursiveCteClause(
    string Name,
    string TableName,
    string? TableSchema,
    string KeyColumn,
    string ParentKeyColumn,
    SqlExpression Roots,
    BlueTuskRecursiveUnionBehavior UnionBehavior);

internal sealed record BlueTuskRowLockingClause(
    BlueTuskRowLockingStrength Strength,
    BlueTuskRowLockingBehavior Behavior);

internal sealed record BlueTuskTableSampleClause(
    BlueTuskTableSampleMethod Method,
    SqlExpression Percentage,
    SqlExpression? Repeatable);
