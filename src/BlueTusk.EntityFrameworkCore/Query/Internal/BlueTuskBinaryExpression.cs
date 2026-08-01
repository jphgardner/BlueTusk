using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskBinaryExpression(
    SqlExpression left,
    SqlExpression right,
    string operatorToken,
    RelationalTypeMapping typeMapping)
    : SqlExpression(typeof(bool), typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public SqlExpression Left { get; } = left;

    public SqlExpression Right { get; } = right;

    public string OperatorToken { get; } = operatorToken;

    public BlueTuskBinaryExpression Update(SqlExpression left, SqlExpression right)
        => left == Left && right == Right
            ? this
            : new BlueTuskBinaryExpression(left, right, OperatorToken, TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(
            (SqlExpression)visitor.Visit(Left),
            (SqlExpression)visitor.Visit(Right));

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskBinaryExpression).GetConstructor(
                [typeof(SqlExpression), typeof(SqlExpression), typeof(string), typeof(RelationalTypeMapping)])!,
            Left.Quote(),
            Right.Quote(),
            Constant(OperatorToken),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(");
        expressionPrinter.Visit(Left);
        expressionPrinter.Append(" ").Append(OperatorToken).Append(" ");
        expressionPrinter.Visit(Right);
        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskBinaryExpression other
            && base.Equals(other)
            && Left.Equals(other.Left)
            && Right.Equals(other.Right)
            && string.Equals(OperatorToken, other.OperatorToken, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), Left, Right, OperatorToken);
}
