using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskRowValueExpression(
    IReadOnlyList<SqlExpression> values,
    Type type,
    RelationalTypeMapping? typeMapping = null)
    : SqlExpression(type, typeMapping)
{
    private static ConstructorInfo? _quotingConstructor;

    public IReadOnlyList<SqlExpression> Values { get; } = values;

    public BlueTuskRowValueExpression Update(IReadOnlyList<SqlExpression> values)
        => values.Count == Values.Count
            && values.Zip(Values, (left, right) => left == right).All(equal => equal)
                ? this
                : new BlueTuskRowValueExpression(values, Type, TypeMapping);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        SqlExpression[]? visitedValues = null;
        for (var index = 0; index < Values.Count; index++)
        {
            var value = Values[index];
            var visited = (SqlExpression)visitor.Visit(value);
            if (visited != value && visitedValues is null)
            {
                visitedValues = new SqlExpression[Values.Count];
                for (var previous = 0; previous < index; previous++)
                {
                    visitedValues[previous] = Values[previous];
                }
            }

            if (visitedValues is not null)
            {
                visitedValues[index] = visited;
            }
        }

        return visitedValues is null ? this : Update(visitedValues);
    }

    public override Expression Quote()
#pragma warning disable EF9100 // Provider expression quoting requires EF's provider-facing helper.
        => New(
            _quotingConstructor ??= typeof(BlueTuskRowValueExpression).GetConstructor(
                [typeof(IReadOnlyList<SqlExpression>), typeof(Type), typeof(RelationalTypeMapping)])!,
            NewArrayInit(typeof(SqlExpression), Values.Select(value => value.Quote())),
            Constant(Type),
            RelationalExpressionQuotingUtilities.QuoteTypeMapping(TypeMapping));
#pragma warning restore EF9100

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("(");
        for (var index = 0; index < Values.Count; index++)
        {
            if (index > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Visit(Values[index]);
        }

        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskRowValueExpression other
            && base.Equals(other)
            && Values.SequenceEqual(other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var value in Values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
