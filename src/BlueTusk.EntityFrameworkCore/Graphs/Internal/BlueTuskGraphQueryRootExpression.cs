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

    public BlueTuskGraphQueryRootExpression(
        IAsyncQueryProvider provider, IEntityType entityType, string sql, object[] values,
        BlueTuskGraphOrderedBranches? orderedBranches)
        : base(provider, entityType)
    {
        Sql = sql;
        Values = Constant(values);
        _types = Array.ConvertAll(values, static value => value.GetType());
        OrderedBranches = orderedBranches;
    }

    private BlueTuskGraphQueryRootExpression(
        IEntityType entityType, string sql, Expression values, Type[] types,
        BlueTuskGraphOrderedBranches? orderedBranches)
        : base(entityType)
    {
        Sql = sql;
        Values = values;
        _types = types;
        OrderedBranches = orderedBranches;
    }

    public string Sql { get; }

    public Expression Values { get; }

    private BlueTuskGraphOrderedBranches? OrderedBranches { get; }

    public BlueTuskGraphSqlArguments CreateSqlArguments() => new(Values, _types, OrderedBranches);

    public BlueTuskGraphQueryRootExpression WithArgumentParameter(ParameterExpression parameter) =>
        new(EntityType, Sql, parameter, _types, OrderedBranches);

    public bool HasSameSqlShape(BlueTuskGraphQueryRootExpression other) =>
        ReferenceEquals(EntityType, other.EntityType) &&
        string.Equals(Sql, other.Sql, StringComparison.Ordinal) &&
#if NET6_0_OR_GREATER
        _types.AsSpan().SequenceEqual(other._types);
#else
        _types.SequenceEqual(other._types);
#endif

    public override Expression DetachQueryProvider() =>
        new BlueTuskGraphQueryRootExpression(EntityType, Sql, Values, _types, OrderedBranches);

#if BLUETUSK_EF7_OR_GREATER
    public override EntityQueryRootExpression UpdateEntityType(IEntityType entityType)
        => entityType.ClrType == EntityType.ClrType && entityType.Name == EntityType.Name
            ? new BlueTuskGraphQueryRootExpression(entityType, Sql, Values, _types, OrderedBranches)
            : throw new InvalidOperationException("Cannot change a typed graph query's entity type.");
#endif

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var values = visitor.Visit(Values);
        return ReferenceEquals(values, Values)
            ? this
            : new BlueTuskGraphQueryRootExpression(EntityType, Sql, values, _types, OrderedBranches);
    }

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) ||
        obj is BlueTuskGraphQueryRootExpression other
        && base.Equals(other)
        && Sql == other.Sql
        && OrderedBranches?.OutputColumn == other.OrderedBranches?.OutputColumn
#if NET6_0_OR_GREATER
        && _types.AsSpan().SequenceEqual(other._types)
#else
        && _types.SequenceEqual(other._types)
#endif
        && ExpressionEqualityComparer.Instance.Equals(Values, other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        hash.Add(Sql, StringComparer.Ordinal);
        hash.Add(OrderedBranches?.OutputColumn, StringComparer.Ordinal);
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

internal sealed class BlueTuskGraphSqlArguments(
    Expression values, Type[] types, BlueTuskGraphOrderedBranches? orderedBranches) : Expression, IPrintableExpression
{
    public Expression Values { get; } = values;

    public ReadOnlySpan<Type> Types => types;

    public BlueTuskGraphOrderedBranches? OrderedBranches { get; } = orderedBranches;

    public override ExpressionType NodeType => ExpressionType.Extension;

    public override Type Type => typeof(object[]);

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visited = visitor.Visit(Values);
        return ReferenceEquals(visited, Values) ? this : new BlueTuskGraphSqlArguments(visited, types, OrderedBranches);
    }

    public void Print(ExpressionPrinter expressionPrinter) => expressionPrinter.Visit(Values);
}

internal sealed class BlueTuskGraphOrderedBranches(string outputColumn, string[] branches)
{
    public string OutputColumn { get; } = outputColumn;

    public ReadOnlySpan<string> Branches => branches;
}
