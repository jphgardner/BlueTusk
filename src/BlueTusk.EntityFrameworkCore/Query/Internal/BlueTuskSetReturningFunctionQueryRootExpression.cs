using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskSetReturningFunctionQueryRootExpression(
    string name,
    Type elementType,
    IReadOnlyList<Expression> arguments,
    IReadOnlyList<string?> argumentStoreTypes,
    string? resultStoreType,
    bool isNullable,
    bool withOrdinality)
    : QueryRootExpression(elementType)
{
    public string Name { get; } = name;

    public IReadOnlyList<Expression> Arguments { get; } = arguments;

    public IReadOnlyList<string?> ArgumentStoreTypes { get; } = argumentStoreTypes;

    public string? ResultStoreType { get; } = resultStoreType;

    public bool IsNullable { get; } = isNullable;

    public bool WithOrdinality { get; } = withOrdinality;

    public override Expression DetachQueryProvider()
        => this;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        Expression[]? visitedArguments = null;
        for (var index = 0; index < Arguments.Count; index++)
        {
            var argument = Arguments[index];
            var visited = visitor.Visit(argument);
            if (visited != argument && visitedArguments is null)
            {
                visitedArguments = new Expression[Arguments.Count];
                for (var previous = 0; previous < index; previous++)
                {
                    visitedArguments[previous] = Arguments[previous];
                }
            }

            if (visitedArguments is not null)
            {
                visitedArguments[index] = visited;
            }
        }

        return visitedArguments is null
            ? this
            : new BlueTuskSetReturningFunctionQueryRootExpression(
                Name,
                ElementType,
                visitedArguments,
                ArgumentStoreTypes,
                ResultStoreType,
                IsNullable,
                WithOrdinality);
    }

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append(Name).Append("(");
        for (var index = 0; index < Arguments.Count; index++)
        {
            if (index > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Visit(Arguments[index]);
        }

        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskSetReturningFunctionQueryRootExpression other
            && base.Equals(other)
            && Name == other.Name
            && Arguments.SequenceEqual(other.Arguments)
            && ArgumentStoreTypes.SequenceEqual(other.ArgumentStoreTypes)
            && ResultStoreType == other.ResultStoreType
            && IsNullable == other.IsNullable
            && WithOrdinality == other.WithOrdinality;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(ResultStoreType, StringComparer.Ordinal);
        hash.Add(IsNullable);
        hash.Add(WithOrdinality);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        foreach (var storeType in ArgumentStoreTypes)
        {
            hash.Add(storeType, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
