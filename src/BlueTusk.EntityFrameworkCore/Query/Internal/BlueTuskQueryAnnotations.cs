using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal static class BlueTuskQueryAnnotationNames
{
    public const string DistinctOn = "BlueTusk:DistinctOn";

    public const string RowLocking = "BlueTusk:RowLocking";

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

internal sealed record BlueTuskRowLockingClause(
    BlueTuskRowLockingStrength Strength,
    BlueTuskRowLockingBehavior Behavior);

internal sealed record BlueTuskTableSampleClause(
    BlueTuskTableSampleMethod Method,
    SqlExpression Percentage,
    SqlExpression? Repeatable);
