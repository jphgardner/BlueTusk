using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Store-value operations that complete EF Core's entity-entry API.</summary>
public static class BlueTuskEntityEntryExtensions
{
    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

    private static readonly MethodInfo SetMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

    private static readonly MethodInfo SharedTypeSetMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), [typeof(string)])!;

    /// <summary>
    /// Queries the database for the values of the tracked entity as they currently exist in the database,
    /// including its complex collections.
    /// </summary>
    /// <remarks>
    /// EF Core's <see cref="EntityEntry.GetDatabaseValues" /> reads scalar columns only, so the store values it
    /// returns have no complex-collection values. This method reads the same scalar values and every complex
    /// collection in a single statement, so all of them come from one database snapshot. For an entity type without
    /// complex collections it returns exactly what <see cref="EntityEntry.GetDatabaseValues" /> returns.
    /// </remarks>
    /// <param name="entry">The tracked entity entry.</param>
    /// <returns>The store values, or <see langword="null" /> if the entity does not exist in the database.</returns>
    public static PropertyValues? GetCompleteDatabaseValues(this EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var collections = GetComplexCollections(entry.Metadata);
        if (collections.Count == 0)
        {
            return entry.GetDatabaseValues();
        }

        var query = CreateQuery(entry, collections);
        return query is null ? null : CreateValues(entry, collections, query.FirstOrDefault());
    }

    /// <summary>
    /// Asynchronously queries the database for the values of the tracked entity as they currently exist in the
    /// database, including its complex collections.
    /// </summary>
    /// <remarks>
    /// EF Core's <see cref="EntityEntry.GetDatabaseValuesAsync" /> reads scalar columns only, so the store values it
    /// returns have no complex-collection values. This method reads the same scalar values and every complex
    /// collection in a single statement, so all of them come from one database snapshot. For an entity type without
    /// complex collections it returns exactly what <see cref="EntityEntry.GetDatabaseValuesAsync" /> returns.
    /// </remarks>
    /// <param name="entry">The tracked entity entry.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the query to complete.</param>
    /// <returns>The store values, or <see langword="null" /> if the entity does not exist in the database.</returns>
    public static async Task<PropertyValues?> GetCompleteDatabaseValuesAsync(
        this EntityEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var collections = GetComplexCollections(entry.Metadata);
        if (collections.Count == 0)
        {
            return await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
        }

        var query = CreateQuery(entry, collections);
        return query is null
            ? null
            : CreateValues(entry, collections, await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false));
    }

    private static List<IComplexProperty> GetComplexCollections(IEntityType entityType)
        => entityType.GetFlattenedComplexProperties().Where(property => property.IsCollection).ToList();

    // Mirrors EF Core's EntityFinder store-value query (same root, key predicate, filters and scalar projection) and
    // appends each complex collection to the projection.
    private static IQueryable<object?[]>? CreateQuery(EntityEntry entry, List<IComplexProperty> collections)
    {
        var entityType = entry.Metadata;
        var keyProperties = entityType.FindPrimaryKey()!.Properties;
        var keyValues = new object[keyProperties.Count];
        for (var i = 0; i < keyValues.Length; i++)
        {
            var value = entry.Property(keyProperties[i]).CurrentValue;
            if (value is null)
            {
                return null;
            }

            keyValues[i] = value;
        }

        var parameter = Expression.Parameter(typeof(object), "e");
        var keyBuffer = Expression.Field(Expression.Constant(new KeyBuffer(keyValues)), nameof(KeyBuffer.Values));
        Expression? predicate = null;
        for (var i = 0; i < keyProperties.Count; i++)
        {
            var equals = Infrastructure.ExpressionExtensions.CreateEqualsExpression(
                Access(parameter, keyProperties[i]),
                Expression.Convert(Expression.ArrayIndex(keyBuffer, Expression.Constant(i)), keyProperties[i].ClrType));
            predicate = predicate is null ? equals : Expression.AndAlso(predicate, equals);
        }

        var projections = new List<Expression>();
        foreach (var property in entityType.GetFlattenedProperties())
        {
            projections.Add(
                Expression.Convert(Expression.Convert(Access(parameter, property), property.ClrType), typeof(object)));
        }

        foreach (var collection in collections)
        {
            projections.Add(Expression.Convert(Access(parameter, collection), typeof(object)));
        }

        return CreateRoot(entry.Context, entityType)
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(Expression.Lambda<Func<object, bool>>(predicate!, parameter))
            .Select(Expression.Lambda<Func<object, object?[]>>(Expression.NewArrayInit(typeof(object), projections), parameter));
    }

    private static IQueryable<object> CreateRoot(DbContext context, IEntityType entityType)
    {
        var ownership = entityType.FindOwnership();
        if (ownership is null)
        {
            return entityType.HasSharedClrType
                ? (IQueryable<object>)SharedTypeSetMethod.MakeGenericMethod(entityType.ClrType).Invoke(context, [entityType.Name])!
                : (IQueryable<object>)SetMethod.MakeGenericMethod(entityType.ClrType).Invoke(context, null)!;
        }

        var owner = CreateRoot(context, ownership.PrincipalEntityType);
        var navigation = ownership.PrincipalToDependent!;
        var ownerParameter = Expression.Parameter(ownership.PrincipalEntityType.ClrType, "e");
        var access = Expression.MakeMemberAccess(ownerParameter, navigation.GetMemberInfo(forMaterialization: false, forSet: false));
        var call = navigation.IsCollection
            ? Expression.Call(
                typeof(Queryable),
                nameof(Queryable.SelectMany),
                [ownerParameter.Type, entityType.ClrType],
                owner.Expression,
                Expression.Quote(Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(ownerParameter.Type, typeof(IEnumerable<>).MakeGenericType(entityType.ClrType)),
                    access,
                    ownerParameter)))
            : Expression.Call(
                typeof(Queryable),
                nameof(Queryable.Select),
                [ownerParameter.Type, entityType.ClrType],
                owner.Expression,
                Expression.Quote(Expression.Lambda(access, ownerParameter)));
        return (IQueryable<object>)owner.Provider.CreateQuery(call);
    }

    // EF.Property access from the entity through each containing complex property, as EF Core's finder builds it.
    private static Expression Access(ParameterExpression parameter, IPropertyBase member)
    {
        var path = new List<IPropertyBase> { member };
        while (path[^1].DeclaringType is IComplexType complexType)
        {
            path.Add(complexType.ComplexProperty);
        }

        Expression expression = parameter;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            expression = Expression.Call(
                PropertyMethod.MakeGenericMethod(path[i].ClrType),
                expression,
                Expression.Constant(path[i].Name, typeof(string)));
            if (i != 0 && expression.Type.IsValueType)
            {
                expression = Expression.Convert(expression, typeof(object));
            }
        }

        return expression;
    }

    private static PropertyValues? CreateValues(
        EntityEntry entry,
        List<IComplexProperty> collections,
        object?[]? row)
    {
        if (row is null)
        {
            return null;
        }

        var scalarCount = row.Length - collections.Count;
        var scalars = new object?[scalarCount];
        Array.Copy(row, scalars, scalarCount);

        // The same store-values type, scalar layout and nullable-complex handling that EntityEntry.GetDatabaseValues
        // creates; the complex collections are then set through the public PropertyValues indexer.
#pragma warning disable EF1001 // Internal EF Core API usage.
        PropertyValues values = new ArrayPropertyValues(entry.GetInfrastructure(), scalars, nullComplexPropertyFlags: null);
#pragma warning restore EF1001
        for (var i = 0; i < collections.Count; i++)
        {
            values[collections[i]] = (IList?)row[scalarCount + i];
        }

        return values;
    }

    // Holds the key values outside the expression tree so EF Core parameterizes them instead of inlining literals.
    private sealed class KeyBuffer(object[] values)
    {
        public readonly object[] Values = values;
    }
}
