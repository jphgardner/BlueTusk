#if !BLUETUSK_EF6_OR_GREATER
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Compatibility;

internal static class LegacyTableAnnotations
{
    // EF5 shares a table expression across the clones of a query tree. The
    // annotations follow that expression and are collected with the query.
    private static readonly ConditionalWeakTable<TableExpressionBase, Dictionary<string, IAnnotation>> Values = new();

    public static IAnnotation? FindAnnotation(this TableExpressionBase table, string name) =>
        Values.TryGetValue(table, out var annotations) && annotations.TryGetValue(name, out var annotation)
            ? annotation : null;

    public static TableExpressionBase AddAnnotation(this TableExpressionBase table, string name, object value)
    {
        Values.GetValue(table, static _ => new(StringComparer.Ordinal)).Add(name, new Annotation(name, value));
        return table;
    }
}
#endif
