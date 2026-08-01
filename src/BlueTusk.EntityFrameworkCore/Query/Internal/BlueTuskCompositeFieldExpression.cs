using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskCompositeFieldExpression(
    SqlExpression instance,
    string fieldName,
    Type type,
    RelationalTypeMapping typeMapping)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public SqlExpression Instance { get; } = instance;

    public string FieldName { get; } = fieldName;

    public BlueTuskCompositeFieldExpression Update(SqlExpression instance)
        => instance == Instance
            ? this
            : new BlueTuskCompositeFieldExpression(instance, FieldName, Type, TypeMapping!);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update((SqlExpression)visitor.Visit(Instance));

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskCompositeFieldExpression).GetConstructor(
                [typeof(SqlExpression), typeof(string), typeof(Type), typeof(RelationalTypeMapping)])!,
            Instance.Quote(),
            Constant(FieldName),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(");
        expressionPrinter.Visit(Instance);
        expressionPrinter.Append(").").Append(FieldName);
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskCompositeFieldExpression other
            && base.Equals(other)
            && Instance.Equals(other.Instance)
            && string.Equals(FieldName, other.FieldName, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), Instance, FieldName);
}
