using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// The members of EF Core 10's relational shaper processor that BlueTusk's value-type JSON materialization uses: the
// processor of the shaped query being compiled, its projection index lookup, and its JSON column reader generation.
internal static class ShaperProcessorAccess
{
    private static readonly FieldInfo CurrentField = typeof(RelationalShapedQueryCompilingExpressionVisitor)
            .GetField("_currentShaperProcessor", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw Missing("_currentShaperProcessor");

    private static readonly MethodInfo GetProjectionIndexMethod = CurrentField.FieldType
            .GetMethod("GetProjectionIndex", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(ProjectionBindingExpression)])
        ?? throw Missing("GetProjectionIndex");

    private static readonly MethodInfo GenerateJsonReaderMethod = CurrentField.FieldType
            .GetMethod("GenerateJsonReader", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int), typeof(ITypeBase)])
        ?? throw Missing("GenerateJsonReader");

    public static bool IsProcessor(ExpressionVisitor visitor)
        => visitor.GetType() == CurrentField.FieldType;

    public static object Current(RelationalShapedQueryCompilingExpressionVisitor visitor)
        => CurrentField.GetValue(visitor) ?? throw new InvalidOperationException("EF Core is not processing a shaper.");

    public static object GetProjectionIndex(object processor, ProjectionBindingExpression projectionBinding)
        => GetProjectionIndexMethod.Invoke(processor, [projectionBinding])!;

    public static ParameterExpression GenerateJsonReader(object processor, int jsonColumnIndex, ITypeBase structuralType)
        => (ParameterExpression)GenerateJsonReaderMethod.Invoke(processor, [jsonColumnIndex, structuralType])!;

    private static InvalidOperationException Missing(string member)
        => new($"The EF Core shaper processor member '{member}' that BlueTusk extends was not found.");
}
