using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Graphs;

internal static partial class BlueTuskGraphSqlTranslator
{
    private static readonly ConditionalWeakTable<IModel, PortableTranslationCache> PortableTranslations = new();

    private static bool TryBindTranslationKey(
        BlueTuskPropertyGraphDefinition graph,
        IReadOnlyList<object> pattern,
        IReadOnlyList<BlueTuskGraphProjection> projections,
        Type resultType,
        out TranslationKey key,
        [NotNullWhen(true)] out object? value)
    {
        key = default;
        value = null;

        // Cache the selective one-hop shape. The predicate slot is a direct
        // scalar value, so the cache never holds a delegate, closure or argument.
        if (pattern is not
            [
                BlueTuskGraphVertexPattern
                {
                    LabelExpression: null,
                    Predicate.Body: BinaryExpression comparison,
                } start,
                BlueTuskGraphEdgePattern { LabelExpression: null, MinimumHops: 1, MaximumHops: 1 } edge,
                BlueTuskGraphVertexPattern { LabelExpression: null, Predicate: null } end,
            ] ||
            comparison.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
        {
            return false;
        }

        var memberOnLeft = TryGetParameterMember(comparison.Left, out var leftProperty);
        var memberOnRight = TryGetParameterMember(comparison.Right, out var rightProperty);
        if (memberOnLeft == memberOnRight)
        {
            return false;
        }

        value = (memberOnLeft ? comparison.Right : comparison.Left) switch
        {
            ConstantExpression constant => constant.Value,
            MemberExpression { Member: FieldInfo field, Expression: ConstantExpression closure }
                when field.FieldType == typeof(int) || field.FieldType == typeof(long) ||
                     field.FieldType == typeof(Guid) => field.GetValue(closure.Value),
            _ => null,
        };
        if (value is not (int or long or Guid))
        {
            return false;
        }

        key = new TranslationKey(
            graph, resultType,
            new VertexKey(start.EntityType, start.Variable, start.ElementTableAlias),
            edge,
            new VertexKey(end.EntityType, end.Variable, end.ElementTableAlias),
            (memberOnLeft ? leftProperty : rightProperty)!,
            comparison.NodeType, memberOnLeft, value.GetType(), projections);
        return true;
    }

    private readonly record struct VertexKey(Type EntityType, string Variable, string? Alias);

    private readonly record struct TranslationKey(
        BlueTuskPropertyGraphDefinition Graph,
        Type ResultType,
        VertexKey Start,
        BlueTuskGraphEdgePattern Edge,
        VertexKey End,
        string Property,
        ExpressionType Operator,
        bool MemberOnLeft,
        Type ArgumentType,
        IReadOnlyList<BlueTuskGraphProjection> Projections)
    {
        public bool Equals(TranslationKey other) =>
            ReferenceEquals(Graph, other.Graph) && ResultType == other.ResultType &&
            Start == other.Start && Edge == other.Edge && End == other.End &&
            Property == other.Property && Operator == other.Operator && MemberOnLeft == other.MemberOnLeft &&
            ArgumentType == other.ArgumentType && Projections.SequenceEqual(other.Projections);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(RuntimeHelpers.GetHashCode(Graph));
            hash.Add(ResultType);
            hash.Add(Start);
            hash.Add(Edge);
            hash.Add(End);
            hash.Add(Property, StringComparer.Ordinal);
            hash.Add(Operator);
            hash.Add(MemberOnLeft);
            hash.Add(ArgumentType);
            foreach (var projection in Projections)
            {
                hash.Add(projection);
            }

            return hash.ToHashCode();
        }
    }

    private sealed record TranslationTemplate(string Sql, BlueTuskGraphQueryImpactPlan ImpactPlan);

    private sealed class PortableTranslationCache
    {
        private const int Capacity = 128;
        private readonly Dictionary<TranslationKey, TranslationTemplate> _entries = [];

        public bool TryGet(TranslationKey key, [NotNullWhen(true)] out TranslationTemplate? template)
        {
            lock (_entries)
            {
                return _entries.TryGetValue(key, out template);
            }
        }

        public void Add(TranslationKey key, GraphTranslation translation)
        {
            lock (_entries)
            {
                if (_entries.Count < Capacity)
                {
                    _entries.TryAdd(key with { Projections = key.Projections.ToArray() },
                        new TranslationTemplate(translation.Sql, translation.ImpactPlan));
                }
            }
        }
    }
}
