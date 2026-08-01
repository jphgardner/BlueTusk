using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed record BlueTuskInsertPropertyValue(string PropertyName, Expression Value);

internal sealed class BlueTuskInsertOnConflictQueryRootExpression(
    IEntityType entityType,
    IReadOnlyList<BlueTuskInsertPropertyValue> values,
    IReadOnlyList<string> conflictProperties,
    IReadOnlyList<string> updateProperties)
    : EntityQueryRootExpression(entityType)
{
    public IReadOnlyList<BlueTuskInsertPropertyValue> Values { get; } = values;

    public IReadOnlyList<string> ConflictProperties { get; } = conflictProperties;

    public IReadOnlyList<string> UpdateProperties { get; } = updateProperties;

    public override Expression DetachQueryProvider()
        => new BlueTuskInsertOnConflictQueryRootExpression(
            EntityType,
            Values,
            ConflictProperties,
            UpdateProperties);

    public override EntityQueryRootExpression UpdateEntityType(IEntityType entityType)
    {
        if (entityType.ClrType != EntityType.ClrType || entityType.Name != EntityType.Name)
        {
            return base.UpdateEntityType(entityType);
        }

        return new BlueTuskInsertOnConflictQueryRootExpression(
            entityType,
            Values,
            ConflictProperties,
            UpdateProperties);
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        BlueTuskInsertPropertyValue[]? visitedValues = null;
        for (var index = 0; index < Values.Count; index++)
        {
            var visitedValue = visitor.Visit(Values[index].Value);
            if (visitedValue != Values[index].Value && visitedValues is null)
            {
                visitedValues = Values.ToArray();
            }

            if (visitedValues is not null)
            {
                visitedValues[index] = Values[index] with { Value = visitedValue };
            }
        }

        return visitedValues is null
            ? this
            : new BlueTuskInsertOnConflictQueryRootExpression(
                EntityType,
                visitedValues,
                ConflictProperties,
                UpdateProperties);
    }

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("InsertOnConflict<")
            .Append(EntityType.ClrType.Name)
            .Append(">(");
        for (var index = 0; index < Values.Count; index++)
        {
            if (index > 0)
            {
                expressionPrinter.Append(", ");
            }

            expressionPrinter.Append(Values[index].PropertyName).Append(" = ");
            expressionPrinter.Visit(Values[index].Value);
        }

        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskInsertOnConflictQueryRootExpression other
            && base.Equals(other)
            && Values.SequenceEqual(other.Values)
            && ConflictProperties.SequenceEqual(other.ConflictProperties)
            && UpdateProperties.SequenceEqual(other.UpdateProperties);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var value in Values)
        {
            hash.Add(value);
        }

        foreach (var property in ConflictProperties)
        {
            hash.Add(property, StringComparer.Ordinal);
        }

        foreach (var property in UpdateProperties)
        {
            hash.Add(property, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
