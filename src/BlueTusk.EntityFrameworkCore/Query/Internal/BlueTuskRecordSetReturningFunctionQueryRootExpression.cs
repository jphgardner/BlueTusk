using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskRecordSetReturningFunctionQueryRootExpression(
    string name,
    Type elementType,
    IReadOnlyList<Expression> arguments,
    IReadOnlyList<string?> argumentStoreTypes,
    IReadOnlyList<BlueTuskSetReturningFunctionColumn> columns)
    : QueryRootExpression(elementType)
{
    public string Name { get; } = name;

    public IReadOnlyList<Expression> Arguments { get; } = arguments;

    public IReadOnlyList<string?> ArgumentStoreTypes { get; } = argumentStoreTypes;

    public IReadOnlyList<BlueTuskSetReturningFunctionColumn> Columns { get; } = columns;

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
            : new BlueTuskRecordSetReturningFunctionQueryRootExpression(
                Name,
                ElementType,
                visitedArguments,
                ArgumentStoreTypes,
                Columns);
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
        => obj is BlueTuskRecordSetReturningFunctionQueryRootExpression other
            && base.Equals(other)
            && Name == other.Name
            && Arguments.SequenceEqual(other.Arguments)
            && ArgumentStoreTypes.SequenceEqual(other.ArgumentStoreTypes)
            && Columns.SequenceEqual(other.Columns);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Name, StringComparer.Ordinal);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        foreach (var storeType in ArgumentStoreTypes)
        {
            hash.Add(storeType, StringComparer.Ordinal);
        }

        foreach (var column in Columns)
        {
            hash.Add(column);
        }

        return hash.ToHashCode();
    }
}
