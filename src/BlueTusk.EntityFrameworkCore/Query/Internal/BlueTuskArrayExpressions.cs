using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskArrayConstructorExpression(
    IReadOnlyList<SqlExpression> rows,
    Type type,
    RelationalTypeMapping typeMapping)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public IReadOnlyList<SqlExpression> Rows { get; } = rows;

    public BlueTuskArrayConstructorExpression Update(IReadOnlyList<SqlExpression> rows)
        => rows.SequenceEqual(Rows)
            ? this
            : new BlueTuskArrayConstructorExpression(rows, Type, TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(Rows.Select(row => (SqlExpression)visitor.Visit(row)).ToArray());

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskArrayConstructorExpression).GetConstructor(
                [typeof(IReadOnlyList<SqlExpression>), typeof(Type), typeof(RelationalTypeMapping)])!,
            NewArrayInit(typeof(SqlExpression), Rows.Select(row => row.Quote())),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("ARRAY[");
        expressionPrinter.VisitCollection(Rows);
        expressionPrinter.Append("]");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskArrayConstructorExpression other
            && base.Equals(other)
            && Rows.SequenceEqual(other.Rows);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var row in Rows)
        {
            hash.Add(row);
        }

        return hash.ToHashCode();
    }
}

internal sealed class BlueTuskArraySubscriptExpression(
    SqlExpression array,
    IReadOnlyList<SqlExpression> subscripts,
    Type type,
    RelationalTypeMapping typeMapping)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public SqlExpression Array { get; } = array;

    public IReadOnlyList<SqlExpression> Subscripts { get; } = subscripts;

    public BlueTuskArraySubscriptExpression Update(
        SqlExpression array,
        IReadOnlyList<SqlExpression> subscripts)
        => array == Array && subscripts.SequenceEqual(Subscripts)
            ? this
            : new BlueTuskArraySubscriptExpression(array, subscripts, Type, TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(
            (SqlExpression)visitor.Visit(Array),
            Subscripts.Select(subscript => (SqlExpression)visitor.Visit(subscript)).ToArray());

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskArraySubscriptExpression).GetConstructor(
            [
                typeof(SqlExpression),
                typeof(IReadOnlyList<SqlExpression>),
                typeof(Type),
                typeof(RelationalTypeMapping),
            ])!,
            Array.Quote(),
            NewArrayInit(typeof(SqlExpression), Subscripts.Select(subscript => subscript.Quote())),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(");
        expressionPrinter.Visit(Array);
        expressionPrinter.Append(")");
        foreach (var subscript in Subscripts)
        {
            expressionPrinter.Append("[");
            expressionPrinter.Visit(subscript);
            expressionPrinter.Append("]");
        }
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskArraySubscriptExpression other
            && base.Equals(other)
            && Array.Equals(other.Array)
            && Subscripts.SequenceEqual(other.Subscripts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Array);
        foreach (var subscript in Subscripts)
        {
            hash.Add(subscript);
        }

        return hash.ToHashCode();
    }
}

internal sealed class BlueTuskArraySliceExpression(
    SqlExpression array,
    IReadOnlyList<SqlExpression> lowerBounds,
    IReadOnlyList<SqlExpression> upperBounds,
    Type type,
    RelationalTypeMapping typeMapping)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public SqlExpression Array { get; } = array;

    public IReadOnlyList<SqlExpression> LowerBounds { get; } = lowerBounds;

    public IReadOnlyList<SqlExpression> UpperBounds { get; } = upperBounds;

    public BlueTuskArraySliceExpression Update(
        SqlExpression array,
        IReadOnlyList<SqlExpression> lowerBounds,
        IReadOnlyList<SqlExpression> upperBounds)
        => array == Array
            && lowerBounds.SequenceEqual(LowerBounds)
            && upperBounds.SequenceEqual(UpperBounds)
                ? this
                : new BlueTuskArraySliceExpression(
                    array,
                    lowerBounds,
                    upperBounds,
                    Type,
                    TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(
            (SqlExpression)visitor.Visit(Array),
            LowerBounds.Select(bound => (SqlExpression)visitor.Visit(bound)).ToArray(),
            UpperBounds.Select(bound => (SqlExpression)visitor.Visit(bound)).ToArray());

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskArraySliceExpression).GetConstructor(
            [
                typeof(SqlExpression),
                typeof(IReadOnlyList<SqlExpression>),
                typeof(IReadOnlyList<SqlExpression>),
                typeof(Type),
                typeof(RelationalTypeMapping),
            ])!,
            Array.Quote(),
            NewArrayInit(typeof(SqlExpression), LowerBounds.Select(bound => bound.Quote())),
            NewArrayInit(typeof(SqlExpression), UpperBounds.Select(bound => bound.Quote())),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(");
        expressionPrinter.Visit(Array);
        expressionPrinter.Append(")");
        for (var index = 0; index < LowerBounds.Count; index++)
        {
            expressionPrinter.Append("[");
            expressionPrinter.Visit(LowerBounds[index]);
            expressionPrinter.Append(":");
            expressionPrinter.Visit(UpperBounds[index]);
            expressionPrinter.Append("]");
        }
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskArraySliceExpression other
            && base.Equals(other)
            && Array.Equals(other.Array)
            && LowerBounds.SequenceEqual(other.LowerBounds)
            && UpperBounds.SequenceEqual(other.UpperBounds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Array);
        foreach (var bound in LowerBounds)
        {
            hash.Add(bound);
        }

        foreach (var bound in UpperBounds)
        {
            hash.Add(bound);
        }

        return hash.ToHashCode();
    }
}
