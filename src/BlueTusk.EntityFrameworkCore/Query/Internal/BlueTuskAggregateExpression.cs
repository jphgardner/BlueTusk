using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskAggregateExpression(
    string? schema,
    string name,
    IReadOnlyList<SqlExpression> arguments,
    bool isDistinct,
    IReadOnlyList<OrderingExpression> orderings,
    IReadOnlyList<OrderingExpression> withinGroupOrderings,
    SqlExpression? predicate,
    Type type,
    RelationalTypeMapping typeMapping)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public string? Schema { get; } = schema;

    public string Name { get; } = name;

    public IReadOnlyList<SqlExpression> Arguments { get; } = arguments;

    public bool IsDistinct { get; } = isDistinct;

    public IReadOnlyList<OrderingExpression> Orderings { get; } = orderings;

    public IReadOnlyList<OrderingExpression> WithinGroupOrderings { get; } = withinGroupOrderings;

    public SqlExpression? Predicate { get; } = predicate;

    public BlueTuskAggregateExpression Update(
        IReadOnlyList<SqlExpression> arguments,
        IReadOnlyList<OrderingExpression> orderings,
        IReadOnlyList<OrderingExpression> withinGroupOrderings,
        SqlExpression? predicate)
        => arguments.SequenceEqual(Arguments)
            && orderings.SequenceEqual(Orderings)
            && withinGroupOrderings.SequenceEqual(WithinGroupOrderings)
            && predicate == Predicate
                ? this
                : new BlueTuskAggregateExpression(
                    Schema,
                    Name,
                    arguments,
                    IsDistinct,
                    orderings,
                    withinGroupOrderings,
                    predicate,
                    Type,
                    TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var arguments = Arguments
            .Select(argument => (SqlExpression)visitor.Visit(argument))
            .ToArray();
        var orderings = Orderings
            .Select(ordering => ordering.Update((SqlExpression)visitor.Visit(ordering.Expression)))
            .ToArray();
        var withinGroupOrderings = WithinGroupOrderings
            .Select(ordering => ordering.Update((SqlExpression)visitor.Visit(ordering.Expression)))
            .ToArray();
        var predicate = Predicate is null
            ? null
            : (SqlExpression)visitor.Visit(Predicate);

        return Update(arguments, orderings, withinGroupOrderings, predicate);
    }

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskAggregateExpression).GetConstructor(
            [
                typeof(string),
                typeof(string),
                typeof(IReadOnlyList<SqlExpression>),
                typeof(bool),
                typeof(IReadOnlyList<OrderingExpression>),
                typeof(IReadOnlyList<OrderingExpression>),
                typeof(SqlExpression),
                typeof(Type),
                typeof(RelationalTypeMapping),
            ])!,
            Constant(Schema, typeof(string)),
            Constant(Name),
            NewArrayInit(typeof(SqlExpression), Arguments.Select(argument => argument.Quote())),
            Constant(IsDistinct),
            NewArrayInit(typeof(OrderingExpression), Orderings.Select(ordering => ordering.Quote())),
            NewArrayInit(
                typeof(OrderingExpression),
                WithinGroupOrderings.Select(ordering => ordering.Quote())),
            Predicate?.Quote() ?? Constant(null, typeof(SqlExpression)),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        if (Schema is not null)
        {
            expressionPrinter.Append(Schema).Append(".");
        }

        expressionPrinter.Append(Name).Append("(");
        if (IsDistinct)
        {
            expressionPrinter.Append("DISTINCT ");
        }

        expressionPrinter.VisitCollection(Arguments);
        if (Orderings.Count > 0)
        {
            expressionPrinter.Append(" ORDER BY ");
            expressionPrinter.VisitCollection(Orderings);
        }

        expressionPrinter.Append(")");
        if (WithinGroupOrderings.Count > 0)
        {
            expressionPrinter.Append(" WITHIN GROUP (ORDER BY ");
            expressionPrinter.VisitCollection(WithinGroupOrderings);
            expressionPrinter.Append(")");
        }

        if (Predicate is not null)
        {
            expressionPrinter.Append(" FILTER (WHERE ");
            expressionPrinter.Visit(Predicate);
            expressionPrinter.Append(")");
        }
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskAggregateExpression other
            && base.Equals(other)
            && string.Equals(Schema, other.Schema, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && IsDistinct == other.IsDistinct
            && Arguments.SequenceEqual(other.Arguments)
            && Orderings.SequenceEqual(other.Orderings)
            && WithinGroupOrderings.SequenceEqual(other.WithinGroupOrderings)
            && Equals(Predicate, other.Predicate);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Schema, StringComparer.Ordinal);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(IsDistinct);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        foreach (var ordering in Orderings)
        {
            hash.Add(ordering);
        }

        foreach (var ordering in WithinGroupOrderings)
        {
            hash.Add(ordering);
        }

        hash.Add(Predicate);
        return hash.ToHashCode();
    }
}
