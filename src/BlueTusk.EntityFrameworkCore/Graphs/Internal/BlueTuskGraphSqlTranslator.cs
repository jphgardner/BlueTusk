using System.Diagnostics;
using System.Linq.Expressions;
using System.Text;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Graphs;

internal static class BlueTuskGraphSqlTranslator
{
    public static GraphTranslation Translate(
        DbContext context,
        BlueTuskPropertyGraphDefinition graph,
        IReadOnlyList<object> pattern,
        IReadOnlyList<BlueTuskGraphProjection> projections,
        Type resultType)
    {
        if (context.Database.GetDbConnection() is BlueTuskConnection { SupportsSqlPgq: false })
        {
            throw new BlueTuskGraphTranslationException(
                "Typed property-graph queries require PostgreSQL 19 or later; " +
                "the current open connection does not support SQL/PGQ.");
        }

        if (projections.Count == 0)
        {
            throw new BlueTuskGraphTranslationException(
                "A graph query must project at least one typed property.");
        }

        var helper = context.GetService<ISqlGenerationHelper>();
        var resolved = pattern.Select(step => ResolveStep(context.Model, graph, step)).ToArray();
        ValidatePath(resolved);
        var variables = resolved.ToDictionary(step => step.Variable, StringComparer.Ordinal);
        var resultEntityType = context.Model.FindEntityType(resultType);
        ValidateEntityProjection(resultEntityType, projections);
        var columns = new List<GraphColumn>();
        foreach (var projection in projections)
        {
            if (!variables.TryGetValue(projection.Variable, out var variable))
            {
                throw new BlueTuskGraphTranslationException(
                    $"Projection references unknown graph variable '{projection.Variable}'.");
            }

            if (variable.EntityType.ClrType != projection.ElementType)
            {
                throw new BlueTuskGraphTranslationException(
                    $"Graph variable '{projection.Variable}' represents '{variable.EntityType.ClrType.Name}', " +
                    $"not '{projection.ElementType.Name}'.");
            }

            var propertyName = ResolveGraphProperty(variable, projection.GraphProperty);
            columns.Add(new GraphColumn(
                variable.Variable,
                propertyName,
                ResolveOutputName(resultEntityType, projection.ResultProperty),
                IsHidden: false));
        }

        var parameters = new List<object>();
        var predicates = new List<string>();
        foreach (var variable in resolved.OfType<ResolvedVertex>().Where(vertex => vertex.Predicate is not null))
        {
            predicates.Add(TranslatePredicate(
                variable,
                variable.Predicate!.Body,
                helper,
                columns,
                parameters));
        }

        var sql = new StringBuilder("SELECT ");
        AppendDelimited(
            sql,
            columns.Where(column => !column.IsHidden).Select(column => helper.DelimitIdentifier(column.OutputName)));
        sql.Append(" FROM GRAPH_TABLE (")
            .Append(helper.DelimitIdentifier(graph.Name, graph.Schema))
            .Append(" MATCH ");
        AppendPattern(sql, resolved, helper);
        sql.Append(" COLUMNS (");
        AppendDelimited(
            sql,
            columns.Select(column =>
                $"{helper.DelimitIdentifier(column.Variable)}.{helper.DelimitIdentifier(column.Property)} " +
                $"AS {helper.DelimitIdentifier(column.OutputName)}"));
        sql.Append("))");
        if (predicates.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }

        return new GraphTranslation(sql.ToString(), parameters.ToArray());
    }

    private static ResolvedStep ResolveStep(
        IModel model,
        BlueTuskPropertyGraphDefinition graph,
        object step)
    {
        var (entityClrType, variable, alias, kind) = step switch
        {
            BlueTuskGraphVertexPattern vertex =>
                (vertex.EntityType, vertex.Variable, vertex.ElementTableAlias, BlueTuskGraphElementKind.Vertex),
            BlueTuskGraphEdgePattern edge =>
                (edge.EntityType, edge.Variable, edge.ElementTableAlias, BlueTuskGraphElementKind.Edge),
            _ => throw new BlueTuskGraphTranslationException(
                $"Unsupported graph pattern node '{step.GetType().Name}'."),
        };
        var entityType = model.FindEntityType(entityClrType)
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity type '{entityClrType.Name}' is not part of the EF model.");
        var table = entityType.GetTableName()
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity type '{entityType.DisplayName()}' is not mapped to a table.");
        var schema = entityType.GetSchema();
        var candidates = graph.ElementTables.Where(element =>
            element.Kind == kind &&
            string.Equals(element.Table, table, StringComparison.Ordinal) &&
            string.Equals(element.Schema, schema, StringComparison.Ordinal) &&
            (alias is null || string.Equals(element.Alias, alias, StringComparison.Ordinal))).ToArray();
        if (candidates.Length != 1)
        {
            throw new BlueTuskGraphTranslationException(
                $"Graph variable '{variable}' must resolve to exactly one {kind.ToString().ToLowerInvariant()} " +
                $"element table for entity type '{entityType.DisplayName()}'.");
        }

        var element = candidates[0];
        if (element.Labels.Count != 1)
        {
            throw new BlueTuskGraphTranslationException(
                $"Element table '{element.Alias}' must have exactly one label for typed pattern translation.");
        }

        return step switch
        {
            BlueTuskGraphVertexPattern vertex => new ResolvedVertex(
                variable, entityType, element, element.Labels[0].Name, vertex.Predicate),
            BlueTuskGraphEdgePattern edge => new ResolvedEdge(
                variable, entityType, element, element.Labels[0].Name, edge.Direction),
            _ => throw new UnreachableException(),
        };
    }

    private static void ValidatePath(IReadOnlyList<ResolvedStep> steps)
    {
        for (var index = 1; index < steps.Count; index += 2)
        {
            var left = (ResolvedVertex)steps[index - 1];
            var edge = (ResolvedEdge)steps[index];
            var right = (ResolvedVertex)steps[index + 1];
            var source = edge.Element.Source
                ?? throw new BlueTuskGraphTranslationException(
                    $"Edge element table '{edge.Element.Alias}' has no source endpoint metadata.");
            var destination = edge.Element.Destination
                ?? throw new BlueTuskGraphTranslationException(
                    $"Edge element table '{edge.Element.Alias}' has no destination endpoint metadata.");
            var expectedSource = edge.Direction == BlueTuskGraphEdgeDirection.Outgoing ? left : right;
            var expectedDestination = edge.Direction == BlueTuskGraphEdgeDirection.Outgoing ? right : left;
            if (!string.Equals(source.VertexTableAlias, expectedSource.Element.Alias, StringComparison.Ordinal) ||
                !string.Equals(destination.VertexTableAlias, expectedDestination.Element.Alias, StringComparison.Ordinal))
            {
                throw new BlueTuskGraphTranslationException(
                    $"Edge '{edge.Variable}' endpoint metadata does not match its {edge.Direction.ToString().ToLowerInvariant()} traversal.");
            }
        }
    }

    private static string ResolveGraphProperty(ResolvedStep variable, string efPropertyName)
    {
        var property = variable.EntityType.FindProperty(efPropertyName)
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity type '{variable.EntityType.DisplayName()}' has no property '{efPropertyName}'.");
        var graphProperties = variable.Element.Labels.SelectMany(label => label.Properties).ToArray();
        var byName = graphProperties.Where(candidate =>
            string.Equals(candidate.Name, property.Name, StringComparison.Ordinal)).ToArray();
        if (byName.Length == 1)
        {
            return byName[0].Name;
        }

        var storeObject = StoreObjectIdentifier.Create(variable.EntityType, StoreObjectType.Table);
        var columnName = storeObject is null ? null : property.GetColumnName(storeObject.Value);
        var byColumn = graphProperties.Where(candidate =>
            candidate.IsColumn && string.Equals(candidate.Expression, columnName, StringComparison.Ordinal)).ToArray();
        return byColumn.Length == 1
            ? byColumn[0].Name
            : throw new BlueTuskGraphTranslationException(
                $"EF property '{property.Name}' is not exposed by label '{variable.Label}'.");
    }

    private static void ValidateEntityProjection(
        IEntityType? resultEntityType,
        IReadOnlyList<BlueTuskGraphProjection> projections)
    {
        if (resultEntityType is null)
        {
            return;
        }

        var projectedProperties = projections
            .Select(projection => projection.ResultProperty)
            .ToHashSet(StringComparer.Ordinal);
        var missingProperties = resultEntityType.GetProperties()
            .Where(property => !projectedProperties.Contains(property.Name))
            .Select(property => property.Name)
            .ToArray();
        if (missingProperties.Length > 0)
        {
            throw new BlueTuskGraphTranslationException(
                $"Entity result '{resultEntityType.DisplayName()}' must project every mapped property. " +
                $"Missing: {string.Join(", ", missingProperties)}.");
        }
    }

    private static string ResolveOutputName(IEntityType? resultEntityType, string resultProperty)
    {
        if (resultEntityType is null)
        {
            return resultProperty;
        }

        var property = resultEntityType.FindProperty(resultProperty)
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity result '{resultEntityType.DisplayName()}' has no mapped property '{resultProperty}'.");
        var storeObject = StoreObjectIdentifier.Create(resultEntityType, StoreObjectType.Table)
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity result '{resultEntityType.DisplayName()}' is not mapped to a table.");
        return property.GetColumnName(storeObject)
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity result property '{property.Name}' has no table column mapping.");
    }

    private static string TranslatePredicate(
        ResolvedVertex variable,
        Expression expression,
        ISqlGenerationHelper helper,
        List<GraphColumn> columns,
        List<object> parameters)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical)
        {
            var logicalOperator = logical.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
            return $"({TranslatePredicate(variable, logical.Left, helper, columns, parameters)} " +
                $"{logicalOperator} {TranslatePredicate(variable, logical.Right, helper, columns, parameters)})";
        }

        if (expression is not BinaryExpression comparison ||
            comparison.NodeType is not (
                ExpressionType.Equal or ExpressionType.NotEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
        {
            throw new BlueTuskGraphTranslationException(
                $"Predicate node '{expression.NodeType}' is not supported. " +
                "Use direct property comparisons joined by && or ||.");
        }

        var memberOnLeft = TryGetParameterMember(comparison.Left, out var propertyName);
        var memberOnRight = TryGetParameterMember(comparison.Right, out var rightPropertyName);
        if (memberOnLeft == memberOnRight)
        {
            throw new BlueTuskGraphTranslationException(
                "A graph predicate comparison must have one direct entity property and one captured or constant value.");
        }

        var valueExpression = memberOnLeft ? comparison.Right : comparison.Left;
        propertyName = memberOnLeft ? propertyName : rightPropertyName;
        var graphProperty = ResolveGraphProperty(variable, propertyName!);
        var hidden = columns.FirstOrDefault(column =>
            column.IsHidden && column.Variable == variable.Variable && column.Property == graphProperty);
        if (hidden is null)
        {
            var hiddenIndex = columns.Count(column => column.IsHidden);
            string hiddenName;
            do
            {
                hiddenName = $"__bluetusk_filter_{hiddenIndex++}";
            }
            while (columns.Any(column =>
                string.Equals(column.OutputName, hiddenName, StringComparison.Ordinal)));

            hidden = new GraphColumn(
                variable.Variable,
                graphProperty,
                hiddenName,
                IsHidden: true);
            columns.Add(hidden);
        }

        var value = Evaluate(valueExpression);
        var columnSql = helper.DelimitIdentifier(hidden.OutputName);
        if (value is null)
        {
            return comparison.NodeType switch
            {
                ExpressionType.Equal => $"{columnSql} IS NULL",
                ExpressionType.NotEqual => $"{columnSql} IS NOT NULL",
                _ => throw new BlueTuskGraphTranslationException(
                    "Only equality and inequality comparisons can use a null graph predicate value."),
            };
        }

        var parameterIndex = parameters.Count;
        parameters.Add(value);
        var sqlOperator = GetSqlOperator(comparison.NodeType, reverse: !memberOnLeft);
        return $"{columnSql} {sqlOperator} {{{parameterIndex}}}";
    }

    private static bool TryGetParameterMember(Expression expression, out string? propertyName)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        if (expression is MemberExpression { Expression: ParameterExpression } member)
        {
            propertyName = member.Member.Name;
            return true;
        }

        propertyName = null;
        return false;
    }

    private static object? Evaluate(Expression expression)
    {
        try
        {
            return Expression.Lambda<Func<object?>>(
                Expression.Convert(expression, typeof(object))).Compile().Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new BlueTuskGraphTranslationException(
                $"Graph predicate value expression '{expression}' could not be evaluated safely.");
        }
    }

    private static string GetSqlOperator(ExpressionType nodeType, bool reverse) =>
        (nodeType, reverse) switch
        {
            (ExpressionType.Equal, _) => "=",
            (ExpressionType.NotEqual, _) => "<>",
            (ExpressionType.GreaterThan, false) or (ExpressionType.LessThan, true) => ">",
            (ExpressionType.GreaterThanOrEqual, false) or (ExpressionType.LessThanOrEqual, true) => ">=",
            (ExpressionType.LessThan, false) or (ExpressionType.GreaterThan, true) => "<",
            (ExpressionType.LessThanOrEqual, false) or (ExpressionType.GreaterThanOrEqual, true) => "<=",
            _ => throw new UnreachableException(),
        };

    private static void AppendPattern(
        StringBuilder sql,
        IReadOnlyList<ResolvedStep> steps,
        ISqlGenerationHelper helper)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            switch (steps[index])
            {
                case ResolvedVertex vertex:
                    sql.Append('(')
                        .Append(helper.DelimitIdentifier(vertex.Variable))
                        .Append(" IS ")
                        .Append(helper.DelimitIdentifier(vertex.Label))
                        .Append(')');
                    break;
                case ResolvedEdge { Direction: BlueTuskGraphEdgeDirection.Outgoing } edge:
                    sql.Append("-[")
                        .Append(helper.DelimitIdentifier(edge.Variable))
                        .Append(" IS ")
                        .Append(helper.DelimitIdentifier(edge.Label))
                        .Append("]->");
                    break;
                case ResolvedEdge edge:
                    sql.Append("<-[")
                        .Append(helper.DelimitIdentifier(edge.Variable))
                        .Append(" IS ")
                        .Append(helper.DelimitIdentifier(edge.Label))
                        .Append("]-");
                    break;
            }
        }
    }

    private static void AppendDelimited(StringBuilder sql, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                sql.Append(", ");
            }

            sql.Append(value);
            first = false;
        }
    }

    internal sealed record GraphTranslation(string Sql, object[] Parameters);

    private abstract record ResolvedStep(
        string Variable,
        IEntityType EntityType,
        BlueTuskGraphElementTableDefinition Element,
        string Label);

    private sealed record ResolvedVertex(
        string Variable,
        IEntityType EntityType,
        BlueTuskGraphElementTableDefinition Element,
        string Label,
        LambdaExpression? Predicate)
        : ResolvedStep(Variable, EntityType, Element, Label);

    private sealed record ResolvedEdge(
        string Variable,
        IEntityType EntityType,
        BlueTuskGraphElementTableDefinition Element,
        string Label,
        BlueTuskGraphEdgeDirection Direction)
        : ResolvedStep(Variable, EntityType, Element, Label);

    private sealed record GraphColumn(
        string Variable,
        string Property,
        string OutputName,
        bool IsHidden);
}
