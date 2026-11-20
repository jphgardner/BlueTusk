using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskJsonToRecordsetQueryRootExpression(
    IEntityType entityType,
    Expression json)
#if BLUETUSK_EF7_OR_GREATER
    : QueryRootExpression(entityType.ClrType)
#else
    : EntityQueryRootExpression(entityType)
#endif
{
#if BLUETUSK_EF7_OR_GREATER
    public IEntityType EntityType { get; } = entityType;
#endif

    public Expression Json { get; } = json;

    public override Expression DetachQueryProvider()
        => this;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visitedJson = visitor.Visit(Json);
        return visitedJson == Json
            ? this
            : new BlueTuskJsonToRecordsetQueryRootExpression(EntityType, visitedJson);
    }

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("jsonb_to_recordset<")
            .Append(EntityType.ClrType.Name)
            .Append(">(");
        expressionPrinter.Visit(Json);
        expressionPrinter.Append(")");
    }

    public override bool Equals(object? obj)
        => obj is BlueTuskJsonToRecordsetQueryRootExpression other
            && base.Equals(other)
            && EntityType == other.EntityType
            && Json.Equals(other.Json);

    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), EntityType, Json);
}
