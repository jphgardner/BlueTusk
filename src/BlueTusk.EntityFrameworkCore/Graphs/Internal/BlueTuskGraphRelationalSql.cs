using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Graphs;

internal static partial class BlueTuskGraphSqlTranslator
{
    private static void AppendRelationalQuery(
        StringBuilder sql,
        IReadOnlyList<ResolvedStep> pattern,
        IReadOnlyList<GraphColumn> columns,
        List<string> predicates,
        Dictionary<string, string> originalAliases,
        string defaultSchema,
        ISqlGenerationHelper helper)
    {
        // Keep aliases for original vertices stable across branches so predicates
        // can reference their table columns directly, even when hop counts vary.
        var aliases = originalAliases;
        if (pattern.Count != originalAliases.Count)
        {
            aliases = new Dictionary<string, string>(originalAliases, StringComparer.Ordinal);
            foreach (var step in pattern)
            {
                if (!aliases.ContainsKey(step.Variable))
                {
                    aliases.Add(step.Variable, GetPortableAlias(aliases.Count));
                }
            }
        }

        sql.Append("SELECT ");
        AppendDelimited(sql, columns.Select(column =>
            $"{aliases[column.Variable]}.{DelimitSqlIdentifier(helper, column.ColumnName ??
                throw new BlueTuskGraphTranslationException(
                    $"Portable graph property '{column.Property}' must map to a table column."))} " +
            $"AS {DelimitSqlIdentifier(helper, column.OutputName)}"));
        sql.Append(" FROM ");
        AppendTable(pattern[0]);

        for (var index = 1; index < pattern.Count; index += 2)
        {
            var left = pattern[index - 1];
            var edge = (ResolvedEdge)pattern[index];
            var right = pattern[index + 1];
            var source = edge.Element.Source!;
            var destination = edge.Element.Destination!;
            var forward = edge.Direction != BlueTuskGraphEdgeDirection.Incoming &&
                left.Element.Alias == source.VertexTableAlias && right.Element.Alias == destination.VertexTableAlias;
            var reverse = edge.Direction != BlueTuskGraphEdgeDirection.Outgoing &&
                left.Element.Alias == destination.VertexTableAlias && right.Element.Alias == source.VertexTableAlias;

            sql.Append(" JOIN ");
            AppendTable(edge);
            sql.Append(" ON ");
            if (forward && reverse)
            {
                var leftSource = EndpointMatch(left, edge, source);
                var leftDestination = EndpointMatch(left, edge, destination);
                var rightSource = EndpointMatch(right, edge, source);
                var rightDestination = EndpointMatch(right, edge, destination);
                // A disjunction matches each tuple once, including a self-loop. The separate
                // predicates on each side let the planner use both endpoint indexes.
                sql.Append("((").Append(leftSource).Append(") OR (").Append(leftDestination).Append(")) JOIN ");
                AppendTable(right);
                sql.Append(" ON ((").Append(rightDestination).Append(") OR (").Append(rightSource)
                    .Append(")) AND ((").Append(leftSource).Append(" AND ").Append(rightDestination)
                    .Append(") OR (").Append(leftDestination).Append(" AND ").Append(rightSource).Append("))");
            }
            else
            {
                sql.Append(EndpointMatch(left, edge, forward ? source : destination)).Append(" JOIN ");
                AppendTable(right);
                sql.Append(" ON ").Append(EndpointMatch(right, edge, forward ? destination : source));
            }
        }

        if (predicates.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", predicates);
        }

        void AppendTable(ResolvedStep step) =>
            sql.Append(DelimitSqlIdentifier(helper, step.Element.Table, step.Element.Schema ?? defaultSchema))
                .Append(" AS ").Append(aliases[step.Variable]);

        string EndpointMatch(ResolvedStep vertex, ResolvedEdge edge, BlueTuskGraphEndpointDefinition endpoint)
        {
            if (endpoint.EdgeKeyColumns.Count == 0 || endpoint.EdgeKeyColumns.Count != endpoint.VertexKeyColumns.Count)
            {
                throw new BlueTuskGraphTranslationException($"Edge '{edge.Variable}' has an invalid endpoint key.");
            }

            return string.Join(" AND ", endpoint.EdgeKeyColumns.Zip(endpoint.VertexKeyColumns, (edgeColumn, vertexColumn) =>
                $"{aliases[edge.Variable]}.{DelimitSqlIdentifier(helper, edgeColumn)} = " +
                $"{aliases[vertex.Variable]}.{DelimitSqlIdentifier(helper, vertexColumn)}"));
        }
    }

    private static Dictionary<string, string> CreatePortableAliases(IReadOnlyList<ResolvedStep> pattern)
    {
        var aliases = new Dictionary<string, string>(pattern.Count, StringComparer.Ordinal);
        for (var index = 0; index < pattern.Count; index++)
        {
            aliases.Add(pattern[index].Variable, GetPortableAlias(index));
        }

        return aliases;
    }

    private static string GetPortableAlias(int index) => $"g{index.ToString(CultureInfo.InvariantCulture)}";

    // EF's raw SQL query roots interpret braces as composite format placeholders.
    private static string DelimitSqlIdentifier(ISqlGenerationHelper helper, string name, string? schema = null) =>
        (schema is null ? helper.DelimitIdentifier(name) : helper.DelimitIdentifier(name, schema))
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
}
