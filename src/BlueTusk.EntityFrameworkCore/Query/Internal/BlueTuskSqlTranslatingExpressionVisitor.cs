using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
#if !BLUETUSK_EF7_OR_GREATER
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
#endif

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskSqlTranslatingExpressionVisitor(
    RelationalSqlTranslatingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext,
    QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor
#if !BLUETUSK_EF7_OR_GREATER
    , IEnumerable<IBlueTuskLegacyAggregateTranslator> aggregateTranslators
#endif
    )
    : RelationalSqlTranslatingExpressionVisitor(
        dependencies,
        queryCompilationContext,
        queryableMethodTranslatingExpressionVisitor)
{
#if !BLUETUSK_EF7_OR_GREATER
    private readonly BlueTuskPostgreSqlAggregateTranslator _legacyAggregates =
        new(dependencies.SqlExpressionFactory, dependencies.TypeMappingSource);
    private readonly ISqlExpressionFactory _legacySqlFactory = dependencies.SqlExpressionFactory;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _legacyLogger = queryCompilationContext.Logger;

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        var translateProviderAggregate = BlueTuskPostgreSqlAggregateTranslator.CanTranslate(method);
        var plugin = translateProviderAggregate ? null : aggregateTranslators.FirstOrDefault(translator => translator.CanTranslate(method));
        if ((translateProviderAggregate || plugin is not null)
            && methodCallExpression.Arguments.Count >= 2
            && TranslateLegacyEnumerable(methodCallExpression.Arguments[1]) is { } enumerable
            && Visit(enumerable.Selector) is SqlExpression selector)
        {
            enumerable.Selector = selector;
            var arguments = new List<SqlExpression> { _legacySqlFactory.Constant(null) };
            for (var index = 2; index < methodCallExpression.Arguments.Count; index++)
            {
                if (Visit(methodCallExpression.Arguments[index]) is not SqlExpression argument)
                {
                    return base.VisitMethodCall(methodCallExpression);
                }

                arguments.Add(argument);
            }

            var aggregate = plugin is null
                ? _legacyAggregates.Translate(method, enumerable, arguments, _legacyLogger)
                : plugin.Translate(method, enumerable, arguments, _legacyLogger);
            if (aggregate is not null)
            {
                return aggregate;
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    private EnumerableExpression? TranslateLegacyEnumerable(Expression expression)
    {
        if (expression is GroupByShaperExpression grouping)
        {
            return new EnumerableExpression(grouping.ElementSelector);
        }

        if (expression is not MethodCallExpression call || call.Arguments.Count == 0
            || call.Method.DeclaringType != typeof(Enumerable)
            || TranslateLegacyEnumerable(call.Arguments[0]) is not { } source)
        {
            return null;
        }

        if (call.Method.Name == nameof(Enumerable.Distinct) && call.Arguments.Count == 1)
        {
            if (source.Selector is not EntityShaperExpression entity || entity.EntityType.FindPrimaryKey() is null)
            {
                source.IsDistinct = true;
            }

            return source;
        }

        if (call.Method.Name == nameof(Enumerable.AsEnumerable))
        {
            return source;
        }

        if (call.Arguments.Count != 2 || (call.Arguments[1] is UnaryExpression { NodeType: ExpressionType.Quote } quote
            ? quote.Operand : call.Arguments[1]) is not LambdaExpression lambda)
        {
            return null;
        }

        var body = ReplacingExpressionVisitor.Replace(lambda.Parameters[0], source.Selector, lambda.Body);
        if (call.Method.Name == nameof(Enumerable.Select))
        {
            source.Selector = body;
            return source;
        }

        if (Translate(body) is not { } sql)
        {
            return null;
        }

        switch (call.Method.Name)
        {
            case nameof(Enumerable.Where):
                source.Predicate = source.Predicate is null ? sql : _legacySqlFactory.AndAlso(source.Predicate, sql);
                return source;
            case nameof(Enumerable.OrderBy):
            case nameof(Enumerable.OrderByDescending):
                source.Orderings.Clear();
                goto case nameof(Enumerable.ThenBy);
            case nameof(Enumerable.ThenBy):
            case nameof(Enumerable.ThenByDescending):
                source.Orderings.Add(new OrderingExpression(sql,
                    call.Method.Name is nameof(Enumerable.OrderBy) or nameof(Enumerable.ThenBy)));
                return source;
            default:
                return null;
        }
    }
#endif

    protected override Expression VisitUnary(UnaryExpression unaryExpression)
    {
        if (unaryExpression is { NodeType: ExpressionType.Convert, Type: var type }
            && type == typeof(ITuple)
            && unaryExpression.Operand.Type.IsAssignableTo(typeof(ITuple)))
        {
            return Visit(unaryExpression.Operand);
        }

        return base.VisitUnary(unaryExpression);
    }
}

#if !BLUETUSK_EF7_OR_GREATER
internal interface IBlueTuskLegacyAggregateTranslatorPlugin
{
    IEnumerable<IBlueTuskLegacyAggregateTranslator> Translators { get; }
}

internal interface IBlueTuskLegacyAggregateTranslator
{
    bool CanTranslate(MethodInfo method);

    SqlExpression? Translate(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger);
}

internal sealed class EnumerableExpression(Expression selector)
{
    public Expression Selector { get; set; } = selector;
    public bool IsDistinct { get; set; }
    public List<OrderingExpression> Orderings { get; } = [];
    public SqlExpression? Predicate { get; set; }
}
#endif
