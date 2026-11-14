using System.Linq.Expressions;
using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
#if BLUETUSK_EF10_OR_GREATER
using System.Globalization;
using System.Text;
#endif
#if !BLUETUSK_EF9_OR_GREATER
using RelationalParameterBasedSqlProcessorParameters = System.Boolean;
#endif
#if BLUETUSK_EF7_OR_GREATER
using BlueTuskSqlQueryExpression = System.Linq.Expressions.Expression;
#else
using BlueTuskSqlQueryExpression = Microsoft.EntityFrameworkCore.Query.SqlExpressions.SelectExpression;
#endif

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskParameterBasedSqlProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
    : RelationalParameterBasedSqlProcessor(dependencies, parameters)
{
#if BLUETUSK_EF10_OR_GREATER
    protected override Expression ProcessSqlNullability(
        Expression selectExpression,
        ParametersCacheDecorator parametersDecorator)
        => new BlueTuskSqlNullabilityProcessor(Dependencies, Parameters)
            .Process(selectExpression, parametersDecorator);

#pragma warning disable EF1001 // Relational parameter expansion is EF's provider extension point.
    protected override Expression ExpandFromSqlParameter(
        Expression queryExpression,
        ParametersCacheDecorator parametersDecorator)
        => new BlueTuskGraphRelationalParameterProcessor(Dependencies).Expand(queryExpression, parametersDecorator);
#pragma warning restore EF1001
#else
    protected override BlueTuskSqlQueryExpression ProcessSqlNullability(
        BlueTuskSqlQueryExpression queryExpression,
        IReadOnlyDictionary<string, object?> parameterValues,
        out bool canCache)
        => new BlueTuskSqlNullabilityProcessor(Dependencies,
#if BLUETUSK_EF9_OR_GREATER
            Parameters)
#else
            UseRelationalNulls)
#endif
            .Process(queryExpression, parameterValues, out canCache);

    protected override BlueTuskSqlQueryExpression ExpandFromSqlParameter(
        BlueTuskSqlQueryExpression queryExpression,
        IReadOnlyDictionary<string, object?> parameterValues,
        out bool canCache)
        => base.ExpandFromSqlParameter((BlueTuskSqlQueryExpression)new GraphArgumentUnwrapper().Visit(queryExpression), parameterValues, out canCache);

    private sealed class GraphArgumentUnwrapper : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression expression) => expression is
            FromSqlExpression { Arguments: BlueTuskGraphSqlArguments arguments } fromSql
                ? fromSql.Update(arguments.Values)
                : base.VisitExtension(expression);
    }
#endif
}

#if BLUETUSK_EF10_OR_GREATER
#pragma warning disable EF1001 // Uses the same relational binding and name allocation as EF's FromSql path.
internal sealed class BlueTuskGraphRelationalParameterProcessor(RelationalParameterBasedSqlProcessorDependencies dependencies)
    : RelationalParameterProcessor(dependencies)
{
    private Dictionary<FromSqlExpression, Expression>? _graphTables;

    protected override Expression VisitExtension(Expression expression)
    {
        if (expression is SelectExpression
            {
                Tables: [FromSqlExpression
                {
                    Arguments: BlueTuskGraphSqlArguments { OrderedBranches: { } branches },
                } table],
                IsDistinct: false,
                Predicate: null,
                GroupBy.Count: 0,
                Having: null,
                Offset: null,
                Limit: not null,
                Orderings: [var ordering],
                Projection: [{ Expression: ColumnExpression projection }],
            } select &&
            ordering.Expression is ColumnExpression orderColumn &&
            orderColumn.TableAlias == table.Alias && orderColumn.Name == branches.OutputColumn &&
            projection.TableAlias == table.Alias && projection.Name == branches.OutputColumn &&
            !select.GetAnnotations().Any() && !table.GetAnnotations().Any())
        {
            // Use the final parameter names after EF's parameter expansion.
            // The outer LIMIT still registers and binds this same parameter.
            var visitedSelect = (SelectExpression)base.VisitExtension(select);
            return ApplyGraphBranchLimit(visitedSelect, branches, ordering.IsAscending);
        }

        if (expression is not FromSqlExpression { Arguments: BlueTuskGraphSqlArguments arguments } fromSql)
        {
            return base.VisitExtension(expression);
        }

        if (arguments.Values is not QueryParameterExpression parameter)
        {
            return base.VisitExtension(fromSql.Update(arguments.Values));
        }

        _graphTables ??= new(ReferenceEqualityComparer.Instance);
        if (_graphTables.TryGetValue(fromSql, out var visited))
        {
            return visited;
        }

        // Graph values have already been extracted into the current EF query
        // context. Only their types and the invariant name enter the template.
        var parameters = new IRelationalParameter[arguments.Types.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var type = arguments.Types[index];
            var mapping = Dependencies.TypeMappingSource.GetMapping(type);
            // The same EF argument can occur more than once in a composed
            // query. Each table occurrence needs distinct command parameters.
            var name = FormattableString.Invariant($"__g{_graphTables.Count}_{index}");
            var sqlParameter = (SqlParameterExpression)base.VisitExtension(new SqlParameterExpression(
                "bluetusk-graph:" + name, name, type, nullable: false, translationMode: null, mapping));
            parameters[index] = new TypeMappedRelationalParameter(sqlParameter.Name, sqlParameter.Name, mapping, nullable: false);
        }

        return _graphTables[fromSql] = fromSql.Update(
            Expression.Constant(new CompositeRelationalParameter(parameter.Name, parameters)));
    }

    private SelectExpression ApplyGraphBranchLimit(
        SelectExpression select, BlueTuskGraphOrderedBranches branches, bool ascending)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var limit = select.Limit switch
        {
            SqlConstantExpression { Value: int and > 0 } constant =>
                ((int)constant.Value).ToString(CultureInfo.InvariantCulture),
            SqlParameterExpression { Type: var type } parameter when type == typeof(int) =>
                helper.GenerateParameterNamePlaceholder(parameter.Name),
            _ => null,
        };
        if (limit is null)
        {
            return select;
        }

        var column = helper.DelimitIdentifier(branches.OutputColumn)
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
        var sql = new StringBuilder("SELECT ").Append(column).Append(" FROM (");
        var branchSql = branches.Branches;
        for (var index = 0; index < branchSql.Length; index++)
        {
            if (index > 0)
            {
                sql.Append(" UNION ALL ");
            }

            sql.Append('(').Append(branchSql[index]).Append(" ORDER BY ").Append(column)
                .Append(ascending ? " ASC LIMIT " : " DESC LIMIT ").Append(limit).Append(')');
        }

        sql.Append(") AS \"__bluetusk_bounded_paths\"");
        var table = (FromSqlExpression)select.Tables[0];
        var boundedTable = new FromSqlExpression(table.Alias, table.Table, sql.ToString(), table.Arguments, annotations: null);
        return select.Update([boundedTable], select.Predicate, select.GroupBy, select.Having,
            select.Projection, select.Orderings, select.Offset, select.Limit);
    }
}
#pragma warning restore EF1001
#endif

internal sealed class BlueTuskSqlNullabilityProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
    : SqlNullabilityProcessor(dependencies, parameters)
{
#if !BLUETUSK_EF8_OR_GREATER
    protected override TableExpressionBase Visit(TableExpressionBase tableExpressionBase) => tableExpressionBase switch
    {
        BlueTuskSetReturningFunctionTableExpression function => function.Update(
            function.Arguments.Select(argument => Visit(argument, out _)).ToArray()),
        BlueTuskUnnestExpression unnest => unnest.Update(Visit(unnest.Array, out _)),
        _ => base.Visit(tableExpressionBase),
    };
#endif

    protected override SqlExpression VisitCustomSqlExpression(
        SqlExpression sqlExpression,
        bool allowOptimizedExpansion,
        out bool nullable)
    {
        if (sqlExpression is BlueTuskAggregateExpression aggregate)
        {
            var arguments = aggregate.Arguments
                .Select(argument => Visit(argument, allowOptimizedExpansion, out _))
                .ToArray();
            var orderings = aggregate.Orderings
                .Select(ordering => ordering.Update(
                    Visit(ordering.Expression, allowOptimizedExpansion, out _)))
                .ToArray();
            var withinGroupOrderings = aggregate.WithinGroupOrderings
                .Select(ordering => ordering.Update(
                    Visit(ordering.Expression, allowOptimizedExpansion, out _)))
                .ToArray();
            var predicate = aggregate.Predicate is null
                ? null
                : Visit(aggregate.Predicate, allowOptimizedExpansion, out _);
            nullable = true;
            return aggregate.Update(arguments, orderings, withinGroupOrderings, predicate);
        }

        if (sqlExpression is BlueTuskWindowFunctionExpression window)
        {
            var arguments = window.Arguments
                .Select(argument => Visit(argument, allowOptimizedExpansion, out _))
                .ToArray();
            var partitions = window.Partitions
                .Select(partition => Visit(partition, allowOptimizedExpansion, out _))
                .ToArray();
            var orderings = window.Orderings
                .Select(ordering => ordering.Update(
                    Visit(ordering.Expression, allowOptimizedExpansion, out _)))
                .ToArray();
            nullable = window.Name is "lag" or "lead" or "first_value" or "last_value" or "nth_value";
            return window.Update(arguments, partitions, orderings);
        }

        if (sqlExpression is BlueTuskQuantifiedComparisonExpression quantifiedComparison)
        {
            var item = Visit(
                quantifiedComparison.Item,
                allowOptimizedExpansion,
                out var itemNullable);
            var array = Visit(
                quantifiedComparison.Array,
                allowOptimizedExpansion,
                out var arrayNullable);
            var elementType = array.Type.GetElementType();
            var elementNullable = elementType is not null
                && (!elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null);
            nullable = itemNullable || arrayNullable || elementNullable;
            return quantifiedComparison.Update(item, array);
        }

        if (sqlExpression is BlueTuskRowValueExpression rowValue)
        {
            var nullableValue = false;
            var values = rowValue.Values
                .Select(value =>
                {
                    var visited = Visit(value, allowOptimizedExpansion, out var valueNullable);
                    nullableValue |= valueNullable;
                    return visited;
                })
                .ToArray();
            nullable = nullableValue;
            return rowValue.Update(values);
        }

        if (sqlExpression is BlueTuskUnaryExpression unary)
        {
            var operand = Visit(unary.Operand, allowOptimizedExpansion, out var operandNullable);
            nullable = operandNullable;
            return unary.Update(operand);
        }

        if (sqlExpression is BlueTuskCompositeFieldExpression compositeField)
        {
            var instance = Visit(compositeField.Instance, allowOptimizedExpansion, out _);
            nullable = true;
            return compositeField.Update(instance);
        }

        if (sqlExpression is BlueTuskArrayConstructorExpression arrayConstructor)
        {
            var rows = arrayConstructor.Rows
                .Select(row => Visit(row, allowOptimizedExpansion, out _))
                .ToArray();
            nullable = true;
            return arrayConstructor.Update(rows);
        }

        if (sqlExpression is BlueTuskArraySubscriptExpression arraySubscript)
        {
            var array = Visit(arraySubscript.Array, allowOptimizedExpansion, out _);
            var subscripts = arraySubscript.Subscripts
                .Select(subscript => Visit(subscript, allowOptimizedExpansion, out _))
                .ToArray();
            nullable = true;
            return arraySubscript.Update(array, subscripts);
        }

        if (sqlExpression is BlueTuskArraySliceExpression arraySlice)
        {
            var array = Visit(arraySlice.Array, allowOptimizedExpansion, out _);
            var lowerBounds = arraySlice.LowerBounds
                .Select(bound => Visit(bound, allowOptimizedExpansion, out _))
                .ToArray();
            var upperBounds = arraySlice.UpperBounds
                .Select(bound => Visit(bound, allowOptimizedExpansion, out _))
                .ToArray();
            nullable = true;
            return arraySlice.Update(array, lowerBounds, upperBounds);
        }

        if (sqlExpression is not BlueTuskBinaryExpression binary)
        {
            return base.VisitCustomSqlExpression(sqlExpression, allowOptimizedExpansion, out nullable);
        }

        var left = Visit(binary.Left, allowOptimizedExpansion, out var leftNullable);
        var right = Visit(binary.Right, allowOptimizedExpansion, out var rightNullable);
        nullable = leftNullable || rightNullable;
        return binary.Update(left, right);
    }
}
