using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskUnaryExpression(
    SqlExpression operand,
    string operatorToken,
    Type resultType,
    RelationalTypeMapping typeMapping)
    : SqlExpression(resultType, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public SqlExpression Operand { get; } = operand;

    public string OperatorToken { get; } = operatorToken;

    public BlueTuskUnaryExpression Update(SqlExpression operand)
        => operand == Operand
            ? this
            : new BlueTuskUnaryExpression(operand, OperatorToken, Type, TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update((SqlExpression)visitor.Visit(Operand));

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskUnaryExpression).GetConstructor(
                [typeof(SqlExpression), typeof(string), typeof(Type), typeof(RelationalTypeMapping)])!,
            Operand.Quote(),
            Constant(OperatorToken),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(").Append(OperatorToken).Append(" ");
        expressionPrinter.Visit(Operand);
        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskUnaryExpression other
            && base.Equals(other)
            && Operand.Equals(other.Operand)
            && string.Equals(OperatorToken, other.OperatorToken, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), Operand, OperatorToken);
}
