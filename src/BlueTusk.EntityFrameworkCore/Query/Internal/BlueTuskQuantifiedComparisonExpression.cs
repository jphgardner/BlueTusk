using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal enum BlueTuskArrayQuantifier
{
    Any,
    All,
}

internal sealed class BlueTuskQuantifiedComparisonExpression(
    SqlExpression item,
    SqlExpression array,
    string operatorToken,
    BlueTuskArrayQuantifier quantifier,
    RelationalTypeMapping typeMapping)
    : SqlExpression(typeof(bool), typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public SqlExpression Item { get; } = item;

    public SqlExpression Array { get; } = array;

    public string OperatorToken { get; } = operatorToken;

    public BlueTuskArrayQuantifier Quantifier { get; } = quantifier;

    public BlueTuskQuantifiedComparisonExpression Update(SqlExpression item, SqlExpression array)
        => item == Item && array == Array
            ? this
            : new BlueTuskQuantifiedComparisonExpression(
                item,
                array,
                OperatorToken,
                Quantifier,
                TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(
            (SqlExpression)visitor.Visit(Item),
            (SqlExpression)visitor.Visit(Array));

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskQuantifiedComparisonExpression).GetConstructor(
                [
                    typeof(SqlExpression),
                    typeof(SqlExpression),
                    typeof(string),
                    typeof(BlueTuskArrayQuantifier),
                    typeof(RelationalTypeMapping),
                ])!,
            Item.Quote(),
            Array.Quote(),
            Constant(OperatorToken),
            Constant(Quantifier),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(");
        expressionPrinter.Visit(Item);
        expressionPrinter.Append(" ").Append(OperatorToken).Append(" ");
        expressionPrinter.Append(Quantifier == BlueTuskArrayQuantifier.Any ? "ANY(" : "ALL(");
        expressionPrinter.Visit(Array);
        expressionPrinter.Append("))");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskQuantifiedComparisonExpression other
            && base.Equals(other)
            && Item.Equals(other.Item)
            && Array.Equals(other.Array)
            && string.Equals(OperatorToken, other.OperatorToken, StringComparison.Ordinal)
            && Quantifier == other.Quantifier;

    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), Item, Array, OperatorToken, Quantifier);
}
