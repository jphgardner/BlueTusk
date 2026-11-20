using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>
/// Associates a typed graph plan with the exact EF query root produced by the builder.
/// </summary>
internal static class BlueTuskGraphQueryCapture
{
    private static readonly ConditionalWeakTable<Expression, Capture> Captures = new();

    public static void Record(DbContext context, Expression root, BlueTuskGraphQueryImpactPlan plan) =>
        Captures.Add(root, new Capture(context, plan));

    public static BlueTuskGraphQueryImpactPlan? Find(DbContext context, Expression root) =>
        Captures.TryGetValue(root, out var capture) && ReferenceEquals(capture.Context, context)
            ? capture.Plan
            : null;

    // Keep existing replay identities stable when the same graph is rendered
    // through the typed parameter path. Only the diagnostic expression changes;
    // provenance is still checked against the original root.
    public static string Describe(Expression expression) =>
        LegacyRootDescription.Instance.Visit(expression).ToString();

    private sealed class LegacyRootDescription : ExpressionVisitor
    {
        public static LegacyRootDescription Instance { get; } = new();

        protected override Expression VisitExtension(Expression node)
        {
#pragma warning disable EF1001 // Preserve the pre-existing Continuous Graph fingerprint format.
            return node is BlueTuskGraphQueryRootExpression graph
                ? new FromSqlQueryRootExpression(graph.EntityType, graph.Sql, graph.Values)
                : base.VisitExtension(node);
#pragma warning restore EF1001
        }
    }

    private sealed record Capture(DbContext Context, BlueTuskGraphQueryImpactPlan Plan);
}
