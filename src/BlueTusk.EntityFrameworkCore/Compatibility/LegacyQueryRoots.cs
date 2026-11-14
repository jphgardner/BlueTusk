#if !BLUETUSK_EF7_OR_GREATER
global using EntityQueryRootExpression = Microsoft.EntityFrameworkCore.Query.QueryRootExpression;
#if !BLUETUSK_EF6_OR_GREATER
global using IReadOnlyAnnotatable = Microsoft.EntityFrameworkCore.Infrastructure.IAnnotatable;
global using IReadOnlyCheckConstraint = Microsoft.EntityFrameworkCore.Metadata.ICheckConstraint;
global using IReadOnlyEntityType = Microsoft.EntityFrameworkCore.Metadata.IEntityType;
global using IReadOnlyModel = Microsoft.EntityFrameworkCore.Metadata.IModel;
global using CheckConstraintBuilder = BlueTusk.EntityFrameworkCore.BlueTuskCheckConstraintBuilder;
global using IReadOnlyModificationCommand = Microsoft.EntityFrameworkCore.Update.ModificationCommand;
global using IColumnModification = Microsoft.EntityFrameworkCore.Update.ColumnModification;
#endif

using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// EF5 expands navigations from entity query roots. Use keyless metadata for
// scalar and record queries, then project their values through Select.
internal abstract class BlueTuskLegacyQueryRootExpression(Type elementType)
    : EntityQueryRootExpression(GetWrapperEntityType(elementType))
{
    private static readonly ConcurrentDictionary<Type, IEntityType> WrapperEntities = new();

    public Type ElementType { get; } = elementType;

    public Expression ProjectResult()
    {
        var wrapper = Expression.Parameter(EntityType.ClrType, "row");
        var selector = Expression.Lambda(Expression.Property(wrapper, nameof(QueryRow<int>.Value)), wrapper);
        return Expression.Call(QueryableMethods.Select.MakeGenericMethod(EntityType.ClrType, ElementType), this, selector);
    }

    public ShapedQueryExpression? WrapResult(ShapedQueryExpression? query)
    {
        if (query is null)
        {
            return null;
        }

        var wrapper = EntityType.ClrType;
        var value = wrapper.GetProperty(nameof(QueryRow<int>.Value))!;
        return query.UpdateShaperExpression(Expression.MemberInit(Expression.New(wrapper),
            Expression.Bind(value, query.ShaperExpression)));
    }

    private static IEntityType GetWrapperEntityType(Type resultType) => WrapperEntities.GetOrAdd(resultType, static type =>
    {
        var wrapper = typeof(QueryRow<>).MakeGenericType(type);
        var builder = new ModelBuilder();
        builder.Entity(wrapper).HasNoKey().Ignore(nameof(QueryRow<int>.Value));
        return builder.FinalizeModel().FindEntityType(wrapper)!;
    });

    private sealed class QueryRow<TResult>
    {
        public TResult Value { get; set; } = default!;
    }
}

internal readonly record struct ColumnValueSetter(
    Microsoft.EntityFrameworkCore.Query.SqlExpressions.ColumnExpression Column,
    Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression Value);
#endif
