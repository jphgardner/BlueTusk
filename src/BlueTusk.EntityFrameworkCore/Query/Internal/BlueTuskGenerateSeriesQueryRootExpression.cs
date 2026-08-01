using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskGenerateSeriesQueryRootExpression(
    Type elementType,
    IReadOnlyList<Expression> arguments)
    : QueryRootExpression(elementType)
{
    public IReadOnlyList<Expression> Arguments { get; } = arguments;

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
            : new BlueTuskGenerateSeriesQueryRootExpression(ElementType, visitedArguments);
    }

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("EF.Functions.GenerateSeries(");
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
        => obj is BlueTuskGenerateSeriesQueryRootExpression other
            && base.Equals(other)
            && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }
}
