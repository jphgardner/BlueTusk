using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskRecursiveCteQueryRootExpression(
    IEntityType entityType,
    string keyProperty,
    string parentKeyProperty,
    Expression roots,
    BlueTuskRecursiveUnionBehavior unionBehavior)
    : EntityQueryRootExpression(entityType)
{
    public string KeyProperty { get; } = keyProperty;

    public string ParentKeyProperty { get; } = parentKeyProperty;

    public Expression Roots { get; } = roots;

    public BlueTuskRecursiveUnionBehavior UnionBehavior { get; } = unionBehavior;

    public override Expression DetachQueryProvider()
        => new BlueTuskRecursiveCteQueryRootExpression(
            EntityType,
            KeyProperty,
            ParentKeyProperty,
            Roots,
            UnionBehavior);

    public override EntityQueryRootExpression UpdateEntityType(IEntityType entityType)
    {
        if (entityType.ClrType != EntityType.ClrType || entityType.Name != EntityType.Name)
        {
            return base.UpdateEntityType(entityType);
        }

        return new BlueTuskRecursiveCteQueryRootExpression(
            entityType,
            KeyProperty,
            ParentKeyProperty,
            Roots,
            UnionBehavior);
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visitedRoots = visitor.Visit(Roots);
        return visitedRoots == Roots
            ? this
            : new BlueTuskRecursiveCteQueryRootExpression(
                EntityType,
                KeyProperty,
                ParentKeyProperty,
                visitedRoots,
                UnionBehavior);
    }

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("RecursiveDescendants<")
            .Append(EntityType.ClrType.Name)
            .Append(">(")
            .Append(KeyProperty)
            .Append(", ")
            .Append(ParentKeyProperty)
            .Append(", ");
        expressionPrinter.Visit(Roots);
        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskRecursiveCteQueryRootExpression other
            && base.Equals(other)
            && KeyProperty == other.KeyProperty
            && ParentKeyProperty == other.ParentKeyProperty
            && Roots.Equals(other.Roots)
            && UnionBehavior == other.UnionBehavior;

    public override int GetHashCode()
        => HashCode.Combine(
            base.GetHashCode(),
            KeyProperty,
            ParentKeyProperty,
            Roots,
            UnionBehavior);
}
