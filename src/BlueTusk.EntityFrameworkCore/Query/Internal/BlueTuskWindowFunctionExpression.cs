using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskWindowFunctionExpression(
    string name,
    IReadOnlyList<SqlExpression> arguments,
    IReadOnlyList<SqlExpression> partitions,
    IReadOnlyList<OrderingExpression> orderings,
    Type type,
    RelationalTypeMapping typeMapping)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public string Name { get; } = name;

    public IReadOnlyList<SqlExpression> Arguments { get; } = arguments;

    public IReadOnlyList<SqlExpression> Partitions { get; } = partitions;

    public IReadOnlyList<OrderingExpression> Orderings { get; } = orderings;

    public BlueTuskWindowFunctionExpression Update(
        IReadOnlyList<SqlExpression> arguments,
        IReadOnlyList<SqlExpression> partitions,
        IReadOnlyList<OrderingExpression> orderings)
        => arguments.SequenceEqual(Arguments)
            && partitions.SequenceEqual(Partitions)
            && orderings.SequenceEqual(Orderings)
                ? this
                : new BlueTuskWindowFunctionExpression(
                    Name,
                    arguments,
                    partitions,
                    orderings,
                    Type,
                    TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var arguments = Arguments
            .Select(argument => (SqlExpression)visitor.Visit(argument))
            .ToArray();
        var partitions = Partitions
            .Select(partition => (SqlExpression)visitor.Visit(partition))
            .ToArray();
        var orderings = Orderings
            .Select(ordering => ordering.Update((SqlExpression)visitor.Visit(ordering.Expression)))
            .ToArray();
        return Update(arguments, partitions, orderings);
    }

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskWindowFunctionExpression).GetConstructor(
            [
                typeof(string),
                typeof(IReadOnlyList<SqlExpression>),
                typeof(IReadOnlyList<SqlExpression>),
                typeof(IReadOnlyList<OrderingExpression>),
                typeof(Type),
                typeof(RelationalTypeMapping),
            ])!,
            Constant(Name),
            NewArrayInit(typeof(SqlExpression), Arguments.Select(argument => argument.Quote())),
            NewArrayInit(typeof(SqlExpression), Partitions.Select(partition => partition.Quote())),
            NewArrayInit(typeof(OrderingExpression), Orderings.Select(ordering => ordering.Quote())),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append(Name).Append("(");
        expressionPrinter.VisitCollection(Arguments);
        expressionPrinter.Append(") OVER (");
        if (Partitions.Count > 0)
        {
            expressionPrinter.Append("PARTITION BY ");
            expressionPrinter.VisitCollection(Partitions);
        }

        if (Orderings.Count > 0)
        {
            if (Partitions.Count > 0)
            {
                expressionPrinter.Append(" ");
            }

            expressionPrinter.Append("ORDER BY ");
            expressionPrinter.VisitCollection(Orderings);
        }

        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskWindowFunctionExpression other
            && base.Equals(other)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && Arguments.SequenceEqual(other.Arguments)
            && Partitions.SequenceEqual(other.Partitions)
            && Orderings.SequenceEqual(other.Orderings);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Name, StringComparer.Ordinal);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        foreach (var partition in Partitions)
        {
            hash.Add(partition);
        }

        foreach (var ordering in Orderings)
        {
            hash.Add(ordering);
        }

        return hash.ToHashCode();
    }
}

internal sealed class BlueTuskWindowOrderingExpression(
    SqlExpression operand,
    bool isAscending)
    : SqlExpression(operand.Type, operand.TypeMapping)
{
    public SqlExpression Operand { get; } = operand;

    public bool IsAscending { get; } = isAscending;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visited = (SqlExpression)visitor.Visit(Operand);
        return visited == Operand
            ? this
            : new BlueTuskWindowOrderingExpression(visited, IsAscending);
    }

    public override Expression Quote()
        => throw new InvalidOperationException(
            "A PostgreSQL window-order marker must be consumed by a window function.");

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Visit(Operand);
        if (!IsAscending)
        {
            expressionPrinter.Append(" DESC");
        }
    }
}
