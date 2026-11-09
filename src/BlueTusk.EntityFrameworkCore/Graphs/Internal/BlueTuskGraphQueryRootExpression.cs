using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Graphs;

// EF cannot cache command templates for arbitrary FromSql object arrays: their
// element types (or DbParameters) can change without changing the query shape.
// A graph supplies scalar values and includes their types in its query shape.
internal sealed class BlueTuskGraphQueryRootExpression : EntityQueryRootExpression
{
    private readonly Type[] _types;

    public BlueTuskGraphQueryRootExpression(IAsyncQueryProvider provider, IEntityType entityType, string sql, object[] values)
        : base(provider, entityType)
    {
        Sql = sql;
        Values = Constant(values);
        _types = Array.ConvertAll(values, static value => value.GetType());
    }

    private BlueTuskGraphQueryRootExpression(IEntityType entityType, string sql, Expression values, Type[] types)
        : base(entityType)
    {
        Sql = sql;
        Values = values;
        _types = types;
    }

    public string Sql { get; }

    public Expression Values { get; }

    public BlueTuskGraphSqlArguments CreateSqlArguments() => new(Values, _types);

    public BlueTuskGraphQueryRootExpression WithArgumentParameter(ParameterExpression parameter) =>
        new(EntityType, Sql, parameter, _types);

    public bool HasSameSqlShape(BlueTuskGraphQueryRootExpression other) =>
        ReferenceEquals(EntityType, other.EntityType) &&
        string.Equals(Sql, other.Sql, StringComparison.Ordinal) &&
        _types.AsSpan().SequenceEqual(other._types);

    public override Expression DetachQueryProvider() => new BlueTuskGraphQueryRootExpression(EntityType, Sql, Values, _types);

    public override EntityQueryRootExpression UpdateEntityType(IEntityType entityType)
        => entityType.ClrType == EntityType.ClrType && entityType.Name == EntityType.Name
            ? new BlueTuskGraphQueryRootExpression(entityType, Sql, Values, _types)
            : throw new InvalidOperationException("Cannot change a typed graph query's entity type.");

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var values = visitor.Visit(Values);
        return ReferenceEquals(values, Values) ? this : new BlueTuskGraphQueryRootExpression(EntityType, Sql, values, _types);
    }

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) ||
        obj is BlueTuskGraphQueryRootExpression other
        && base.Equals(other)
        && Sql == other.Sql
        && _types.AsSpan().SequenceEqual(other._types)
        && ExpressionEqualityComparer.Instance.Equals(Values, other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Sql, StringComparer.Ordinal);
        hash.Add(Values, ExpressionEqualityComparer.Instance);
        foreach (var type in _types)
        {
            hash.Add(type);
        }

        return hash.ToHashCode();
    }

    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        base.Print(expressionPrinter);
        expressionPrinter.Append(".BlueTuskGraph(").Append(Sql).Append(", ");
        expressionPrinter.Visit(Values);
        expressionPrinter.Append(")");
    }
}

internal sealed class BlueTuskGraphSqlArguments(Expression values, Type[] types) : Expression, IPrintableExpression
{
    public Expression Values { get; } = values;

    public ReadOnlySpan<Type> Types => types;

    public override ExpressionType NodeType => ExpressionType.Extension;

    public override Type Type => typeof(object[]);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visited = visitor.Visit(Values);
        return ReferenceEquals(visited, Values) ? this : new BlueTuskGraphSqlArguments(visited, types);
    }

    public void Print(ExpressionPrinter expressionPrinter) => expressionPrinter.Visit(Values);
}
