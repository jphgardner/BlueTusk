using System.Diagnostics;
using System.Linq.Expressions;
using System.Text;
using BlueTusk.Data.Internal;
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
        var providerConnection = context
            .GetService<IProviderServices>()
            .GetConnection(context.Database.GetDbConnection());
        if (providerConnection.Capabilities?.SupportsSqlPgq is false)
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
        ValidatePath(resolved, projections);
        var expandedPatterns = ExpandPatterns(resolved);
        var variables = resolved.ToDictionary(step => step.Variable, StringComparer.Ordinal);
        var resultEntityType = context.Model.FindEntityType(resultType);
        ValidateEntityProjection(resultEntityType, projections);
        var columns = new List<GraphColumn>();
        var impactProjections = new BlueTuskGraphQueryImpactProjection[projections.Count];
        var projectionIndex = 0;
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

            var property = ResolveGraphPropertyDefinition(variable, projection.GraphProperty);
            columns.Add(new GraphColumn(
                variable.Variable,
                property.Name,
                ResolveOutputName(resultEntityType, projection.ResultProperty),
                IsHidden: false));
            impactProjections[projectionIndex++] = new BlueTuskGraphQueryImpactProjection(
                projection.Variable,
                variable.Element.Alias,
                projection.ResultProperty,
                property.Name,
                property.IsColumn ? property.Expression : null);
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

        var sql = new StringBuilder();
        if (expandedPatterns.Count == 1)
        {
            AppendGraphTableQuery(
                sql,
                graph,
                expandedPatterns[0],
                columns,
                predicates,
                helper);
        }
        else
        {
            sql.Append("SELECT ");
            AppendDelimited(
                sql,
                columns.Where(column => !column.IsHidden)
                    .Select(column => helper.DelimitIdentifier(column.OutputName)));
            sql.Append(" FROM (");
            for (var index = 0; index < expandedPatterns.Count; index++)
            {
                if (index > 0)
                {
                    sql.Append(" UNION ALL ");
                }

                AppendGraphTableQuery(
                    sql,
                    graph,
                    expandedPatterns[index],
                    columns,
                    predicates,
                    helper);
            }

            sql.Append(") AS ")
                .Append(helper.DelimitIdentifier("__bluetusk_bounded_paths"));
        }

        var impactElements = new BlueTuskGraphQueryImpactElement[resolved.Length];
        for (var index = 0; index < resolved.Length; index++)
        {
            var step = resolved[index];
            string? sourceVariable = null;
            string? destinationVariable = null;
            if (step is ResolvedEdge edge)
            {
                var left = resolved[index - 1].Variable;
                var right = resolved[index + 1].Variable;
                sourceVariable = edge.Direction switch
                {
                    BlueTuskGraphEdgeDirection.Outgoing => left,
                    BlueTuskGraphEdgeDirection.Incoming => right,
                    _ => null,
                };
                destinationVariable = edge.Direction switch
                {
                    BlueTuskGraphEdgeDirection.Outgoing => right,
                    BlueTuskGraphEdgeDirection.Incoming => left,
                    _ => null,
                };
            }

            impactElements[index] = new BlueTuskGraphQueryImpactElement(
                step.Variable,
                step.Element.Alias,
                step.Element.Kind,
                step.Element.Table,
                step.Element.Schema,
                step.Element.KeyColumns,
                step.Element.Source,
                step.Element.Destination,
                sourceVariable,
                destinationVariable,
                step.Labels,
                step is ResolvedEdge resolvedEdge ? resolvedEdge.Direction : null,
                step is ResolvedEdge pathEdge ? pathEdge.MinimumHops : 1,
                step is ResolvedEdge pathEdgeMaximum ? pathEdgeMaximum.MaximumHops : 1);
        }

        var impactPlan = new BlueTuskGraphQueryImpactPlan(
            graph.Name,
            graph.Schema,
            Array.AsReadOnly(impactElements),
            Array.AsReadOnly(impactProjections));
        return new GraphTranslation(sql.ToString(), parameters.ToArray(), impactPlan);
    }

    private static ResolvedStep ResolveStep(
        IModel model,
        BlueTuskPropertyGraphDefinition graph,
        object step)
    {
        var (entityClrType, variable, alias, kind, labelExpression) = step switch
        {
            BlueTuskGraphVertexPattern vertex =>
                (vertex.EntityType, vertex.Variable, vertex.ElementTableAlias, BlueTuskGraphElementKind.Vertex, vertex.LabelExpression),
            BlueTuskGraphEdgePattern edge =>
                (edge.EntityType, edge.Variable, edge.ElementTableAlias, BlueTuskGraphElementKind.Edge, edge.LabelExpression),
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
        IReadOnlyList<string> labels;
        if (labelExpression is null)
        {
            if (element.Labels.Count != 1)
            {
                throw new BlueTuskGraphTranslationException(
                    $"Element table '{element.Alias}' has multiple labels; select the intended OR expression with LabelsAnyOf.");
            }

            labels = [element.Labels[0].Name];
        }
        else
        {
            var configuredLabels = element.Labels
                .Select(static label => label.Name)
                .ToHashSet(StringComparer.Ordinal);
            var missingLabels = labelExpression.Labels
                .Where(label => !configuredLabels.Contains(label))
                .ToArray();
            if (missingLabels.Length > 0)
            {
                throw new BlueTuskGraphTranslationException(
                    $"Element table '{element.Alias}' does not expose label(s): {string.Join(", ", missingLabels)}.");
            }

            labels = labelExpression.Labels;
        }

        return step switch
        {
            BlueTuskGraphVertexPattern vertex => new ResolvedVertex(
                variable, entityType, element, labels, vertex.Predicate),
            BlueTuskGraphEdgePattern edge => new ResolvedEdge(
                variable,
                entityType,
                element,
                labels,
                edge.Direction,
                edge.MinimumHops,
                edge.MaximumHops),
            _ => throw new UnreachableException(),
        };
    }

    private static void ValidatePath(
        IReadOnlyList<ResolvedStep> steps,
        IReadOnlyList<BlueTuskGraphProjection> projections)
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
            var forward =
                string.Equals(source.VertexTableAlias, left.Element.Alias, StringComparison.Ordinal) &&
                string.Equals(destination.VertexTableAlias, right.Element.Alias, StringComparison.Ordinal);
            var reverse =
                string.Equals(source.VertexTableAlias, right.Element.Alias, StringComparison.Ordinal) &&
                string.Equals(destination.VertexTableAlias, left.Element.Alias, StringComparison.Ordinal);
            var matches = edge.Direction switch
            {
                BlueTuskGraphEdgeDirection.Outgoing => forward,
                BlueTuskGraphEdgeDirection.Incoming => reverse,
                BlueTuskGraphEdgeDirection.Undirected => forward || reverse,
                _ => false,
            };
            if (!matches)
            {
                throw new BlueTuskGraphTranslationException(
                    $"Edge '{edge.Variable}' endpoint metadata does not match its {edge.Direction.ToString().ToLowerInvariant()} traversal.");
            }

            if (edge.MaximumHops > 1)
            {
                if (!string.Equals(source.VertexTableAlias, destination.VertexTableAlias, StringComparison.Ordinal) ||
                    !string.Equals(source.VertexTableAlias, left.Element.Alias, StringComparison.Ordinal) ||
                    !string.Equals(source.VertexTableAlias, right.Element.Alias, StringComparison.Ordinal))
                {
                    throw new BlueTuskGraphTranslationException(
                        $"Bounded path edge '{edge.Variable}' must connect one vertex element table to itself.");
                }

                if (projections.Any(projection => string.Equals(
                        projection.Variable,
                        edge.Variable,
                        StringComparison.Ordinal)))
                {
                    throw new BlueTuskGraphTranslationException(
                        $"Bounded path edge '{edge.Variable}' cannot be projected because a multi-hop match can contain more than one edge.");
                }
            }
        }
    }

    private static string ResolveGraphProperty(ResolvedStep variable, string efPropertyName)
        => ResolveGraphPropertyDefinition(variable, efPropertyName).Name;

    private static BlueTuskGraphPropertyDefinition ResolveGraphPropertyDefinition(
        ResolvedStep variable,
        string efPropertyName)
    {
        var property = variable.EntityType.FindProperty(efPropertyName)
            ?? throw new BlueTuskGraphTranslationException(
                $"Entity type '{variable.EntityType.DisplayName()}' has no property '{efPropertyName}'.");
        var storeObject = StoreObjectIdentifier.Create(variable.EntityType, StoreObjectType.Table);
        var columnName = storeObject is null ? null : property.GetColumnName(storeObject.Value);
        BlueTuskGraphPropertyDefinition? resolved = null;
        foreach (var labelName in variable.Labels)
        {
            BlueTuskGraphLabelDefinition? label = null;
            foreach (var candidateLabel in variable.Element.Labels)
            {
                if (string.Equals(candidateLabel.Name, labelName, StringComparison.Ordinal))
                {
                    label = candidateLabel;
                    break;
                }
            }

            BlueTuskGraphPropertyDefinition? labelProperty = null;
            foreach (var candidate in label?.Properties ?? [])
            {
                if (!string.Equals(candidate.Name, property.Name, StringComparison.Ordinal) &&
                    (!candidate.IsColumn || !string.Equals(
                        candidate.Expression,
                        columnName,
                        StringComparison.Ordinal)))
                {
                    continue;
                }

                if (labelProperty is not null && !Equals(labelProperty, candidate))
                {
                    throw new BlueTuskGraphTranslationException(
                        $"EF property '{property.Name}' is not exposed unambiguously by label '{labelName}'.");
                }

                labelProperty = candidate;
            }

            if (labelProperty is null)
            {
                throw new BlueTuskGraphTranslationException(
                    $"EF property '{property.Name}' is not exposed unambiguously by label '{labelName}'.");
            }

            if (resolved is not null &&
                (!string.Equals(resolved.Name, labelProperty.Name, StringComparison.Ordinal) ||
                 !string.Equals(resolved.Expression, labelProperty.Expression, StringComparison.Ordinal) ||
                 resolved.IsColumn != labelProperty.IsColumn))
            {
                throw new BlueTuskGraphTranslationException(
                    $"EF property '{property.Name}' must have one consistent graph property across every selected label.");
            }

            resolved = labelProperty;
        }

        return resolved ?? throw new BlueTuskGraphTranslationException(
            $"EF property '{property.Name}' is not exposed by the selected graph labels.");
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

    private static List<IReadOnlyList<ResolvedStep>> ExpandPatterns(
        IReadOnlyList<ResolvedStep> steps)
    {
        if (!steps.OfType<ResolvedEdge>().Any(edge =>
                edge.MinimumHops != 1 || edge.MaximumHops != 1))
        {
            return [steps];
        }

        var variantCount = steps.OfType<ResolvedEdge>()
            .Aggregate(
                1,
                (count, edge) => checked(count * (edge.MaximumHops - edge.MinimumHops + 1)));
        if (variantCount > 64)
        {
            throw new BlueTuskGraphTranslationException(
                "Bounded path expansion would generate more than 64 GRAPH_TABLE branches.");
        }

        List<IReadOnlyList<ResolvedStep>> variants = [[steps[0]]];
        for (var index = 1; index < steps.Count; index += 2)
        {
            var edge = (ResolvedEdge)steps[index];
            var destination = (ResolvedVertex)steps[index + 1];
            var next = new List<IReadOnlyList<ResolvedStep>>();
            foreach (var variant in variants)
            {
                for (var hops = edge.MinimumHops; hops <= edge.MaximumHops; hops++)
                {
                    var expanded = new List<ResolvedStep>(variant);
                    for (var hop = 1; hop <= hops; hop++)
                    {
                        expanded.Add(hops == 1
                            ? edge
                            : edge with { Variable = $"__bluetusk_{edge.Variable}_edge_{hop}" });
                        expanded.Add(hop == hops
                            ? destination
                            : destination with
                            {
                                Variable = $"__bluetusk_{edge.Variable}_vertex_{hop}",
                                Predicate = null,
                            });
                    }

                    next.Add(expanded);
                }
            }

            variants = next;
        }

        return variants;
    }

    private static void AppendGraphTableQuery(
        StringBuilder sql,
        BlueTuskPropertyGraphDefinition graph,
        IReadOnlyList<ResolvedStep> pattern,
        IReadOnlyList<GraphColumn> columns,
        List<string> predicates,
        ISqlGenerationHelper helper)
    {
        sql.Append("SELECT ");
        AppendDelimited(
            sql,
            columns.Where(column => !column.IsHidden)
                .Select(column => helper.DelimitIdentifier(column.OutputName)));
        sql.Append(" FROM GRAPH_TABLE (")
            .Append(helper.DelimitIdentifier(graph.Name, graph.Schema))
            .Append(" MATCH ");
        AppendPattern(sql, pattern, helper);
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
    }

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
                        .Append(" IS ");
                    AppendLabelExpression(sql, vertex.Labels, helper);
                    sql.Append(')');
                    break;
                case ResolvedEdge { Direction: BlueTuskGraphEdgeDirection.Outgoing } edge:
                    sql.Append("-[")
                        .Append(helper.DelimitIdentifier(edge.Variable))
                        .Append(" IS ");
                    AppendLabelExpression(sql, edge.Labels, helper);
                    sql.Append("]->");
                    break;
                case ResolvedEdge { Direction: BlueTuskGraphEdgeDirection.Incoming } edge:
                    sql.Append("<-[")
                        .Append(helper.DelimitIdentifier(edge.Variable))
                        .Append(" IS ");
                    AppendLabelExpression(sql, edge.Labels, helper);
                    sql.Append("]-");
                    break;
                case ResolvedEdge edge:
                    sql.Append("-[")
                        .Append(helper.DelimitIdentifier(edge.Variable))
                        .Append(" IS ");
                    AppendLabelExpression(sql, edge.Labels, helper);
                    sql.Append("]-");
                    break;
            }
        }
    }

    private static void AppendLabelExpression(
        StringBuilder sql,
        IReadOnlyList<string> labels,
        ISqlGenerationHelper helper)
    {
        for (var index = 0; index < labels.Count; index++)
        {
            if (index > 0)
            {
                sql.Append('|');
            }

            sql.Append(helper.DelimitIdentifier(labels[index]));
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

    internal sealed record GraphTranslation(
        string Sql,
        object[] Parameters,
        BlueTuskGraphQueryImpactPlan ImpactPlan);

    private abstract record ResolvedStep(
        string Variable,
        IEntityType EntityType,
        BlueTuskGraphElementTableDefinition Element,
        IReadOnlyList<string> Labels);

    private sealed record ResolvedVertex(
        string Variable,
        IEntityType EntityType,
        BlueTuskGraphElementTableDefinition Element,
        IReadOnlyList<string> Labels,
        LambdaExpression? Predicate)
        : ResolvedStep(Variable, EntityType, Element, Labels);

    private sealed record ResolvedEdge(
        string Variable,
        IEntityType EntityType,
        BlueTuskGraphElementTableDefinition Element,
        IReadOnlyList<string> Labels,
        BlueTuskGraphEdgeDirection Direction,
        int MinimumHops,
        int MaximumHops)
        : ResolvedStep(Variable, EntityType, Element, Labels);

    private sealed record GraphColumn(
        string Variable,
        string Property,
        string OutputName,
        bool IsHidden);
}

internal sealed record BlueTuskGraphQueryImpactPlan(
    string GraphName,
    string? GraphSchema,
    IReadOnlyList<BlueTuskGraphQueryImpactElement> Elements,
    IReadOnlyList<BlueTuskGraphQueryImpactProjection> Projections);

internal sealed record BlueTuskGraphQueryImpactElement(
    string Variable,
    string Alias,
    BlueTuskGraphElementKind Kind,
    string Table,
    string? Schema,
    IReadOnlyList<string> KeyColumns,
    BlueTuskGraphEndpointDefinition? Source,
    BlueTuskGraphEndpointDefinition? Destination,
    string? SourceVariable,
    string? DestinationVariable,
    IReadOnlyList<string> Labels,
    BlueTuskGraphEdgeDirection? Direction,
    int MinimumHops,
    int MaximumHops);

internal sealed record BlueTuskGraphQueryImpactProjection(
    string Variable,
    string ElementAlias,
    string ResultProperty,
    string GraphProperty,
    string? ColumnName);
