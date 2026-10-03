using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryTranslationPostprocessorFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies)
    : IQueryTranslationPostprocessorFactory
{
    public QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext)
        => new BlueTuskQueryTranslationPostprocessor(
            dependencies,
            relationalDependencies,
            (RelationalQueryCompilationContext)queryCompilationContext);
}

internal sealed class BlueTuskQueryTranslationPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    RelationalQueryCompilationContext queryCompilationContext)
    : RelationalQueryTranslationPostprocessor(dependencies, relationalDependencies, queryCompilationContext)
{
    protected override Expression ProcessTypeMappings(Expression expression)
        => new BlueTuskTypeMappingPostprocessor(
                Dependencies,
                RelationalDependencies,
                RelationalQueryCompilationContext)
            .Process(expression);
}

/// <summary>
/// Completes the type mappings of collection parameters expanded with PostgreSQL unnest. With
/// <c>ParameterTranslationMode.Parameter</c> the parameter reaches SQL as one array; its element
/// mapping is inferred from the comparison (for example a <c>Contains</c> against a column), or
/// falls back to the CLR element type's default mapping.
/// </summary>
internal sealed class BlueTuskTypeMappingPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    RelationalQueryCompilationContext queryCompilationContext)
    : RelationalTypeMappingPostprocessor(dependencies, relationalDependencies, queryCompilationContext)
{
    private const string UnnestValueColumn = "value";
    private readonly Dictionary<string, Type> _parameterUnnests = new(StringComparer.Ordinal);

    public override Expression Process(Expression expression)
    {
        if (QueryCompilationContext is BlueTuskQueryCompilationContext { HasUntypedCollectionParameters: true })
        {
            new ParameterUnnestScanner(_parameterUnnests).Visit(expression);
        }

        return base.Process(expression);
    }

    protected override Expression VisitExtension(Expression node)
    {
        if (node is BlueTuskUnnestExpression
            {
                Array: SqlParameterExpression { TypeMapping: null } parameter,
            } unnest
            && TryGetInferredTypeMapping(unnest.Alias, UnnestValueColumn, out var elementMapping))
        {
            var collectionMapping = FindCollectionMapping(parameter.Type, elementMapping)
                ?? throw new InvalidOperationException(
                    $"The collection parameter '{parameter.Name}' of type '{parameter.Type.Name}' has " +
                    $"no PostgreSQL array mapping for element store type '{elementMapping.StoreType}'.");
            return unnest.Update(
                RelationalDependencies.SqlExpressionFactory.ApplyTypeMapping(parameter, collectionMapping));
        }

        return base.VisitExtension(node);
    }

    protected override bool TryGetInferredTypeMapping(
        string tableAlias,
        string columnName,
        [NotNullWhen(true)] out RelationalTypeMapping? inferredTypeMapping)
    {
        if (base.TryGetInferredTypeMapping(tableAlias, columnName, out inferredTypeMapping))
        {
            return true;
        }

        if (columnName == UnnestValueColumn
            && _parameterUnnests.TryGetValue(tableAlias, out var elementType))
        {
            inferredTypeMapping = RelationalDependencies.TypeMappingSource.FindMapping(
                elementType,
                QueryCompilationContext.Model);
            return inferredTypeMapping is not null;
        }

        return false;
    }

    private RelationalTypeMapping? FindCollectionMapping(
        Type collectionType,
        RelationalTypeMapping elementMapping)
    {
        var typeMappingSource = RelationalDependencies.TypeMappingSource;
        if (typeMappingSource.FindMapping(
                collectionType,
                QueryCompilationContext.Model,
                elementMapping) is BlueTuskArrayTypeMapping builtInArray)
        {
            return builtInArray;
        }

        // User-defined element types, such as a mapped PostgreSQL enum, resolve through the
        // array of their schema-qualified store type.
        return typeMappingSource.FindMapping(collectionType, $"{elementMapping.StoreType}[]")
            as BlueTuskArrayTypeMapping;
    }

    internal static Type? GetElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType();
        }

        return collectionType.IsGenericType
            && collectionType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? collectionType.GetGenericArguments()[0]
                : collectionType
                    .GetInterfaces()
                    .FirstOrDefault(type => type.IsGenericType
                        && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))?
                    .GetGenericArguments()[0];
    }

    private sealed class ParameterUnnestScanner(Dictionary<string, Type> parameterUnnests)
        : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is ShapedQueryExpression shapedQuery)
            {
                // Shaped queries do not allow generic child visiting; every SQL table
                // expression lives under the query expression.
                Visit(shapedQuery.QueryExpression);
                return node;
            }

            if (node is BlueTuskUnnestExpression
                {
                    Array: SqlParameterExpression { TypeMapping: null } parameter,
                } unnest
                && GetElementType(parameter.Type) is { } elementType)
            {
                parameterUnnests[unnest.Alias] = elementType;
            }

            return base.VisitExtension(node);
        }
    }
}
