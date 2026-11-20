#if !BLUETUSK_EF8_OR_GREATER
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace BlueTusk.EntityFrameworkCore.Graphs;

// EF 5–7 raw SQL cannot map arbitrary result objects. Build a keyless metadata
// model for each DTO and let the regular EF query provider translate and shape it.
internal static class BlueTuskLegacyGraphProjection
{
    private static readonly ConditionalWeakTable<IModel, ConcurrentDictionary<Type, IEntityType>> Projections = new();

    public static IQueryable<TResult> CreateQuery<TResult>(DbContext context, string sql, object[] parameters)
        where TResult : class
    {
        var projections = Projections.GetOrCreateValue(context.Model);
        var entityType = projections.GetOrAdd(typeof(TResult), static (type, scope) => CreateEntity(scope, type), context);
        var provider = context.GetService<IAsyncQueryProvider>();
#pragma warning disable EF1001 // Use EF's own relational query root and keep every caller-supplied SQL value parameterized.
        return provider.CreateQuery<TResult>(new FromSqlQueryRootExpression(
            provider, entityType, sql, Expression.Constant(parameters)));
#pragma warning restore EF1001
    }

    private static IEntityType CreateEntity(DbContext context, Type projection)
    {
        var conventions = ConventionSet.CreateConventionSet(context);
        Remove<DbSetFindingConvention, IModelInitializedConvention>(conventions.ModelInitializedConventions);
        Remove<RelationshipDiscoveryConvention, IEntityTypeAddedConvention>(conventions.EntityTypeAddedConventions);
        Remove<ServicePropertyDiscoveryConvention, IEntityTypeAddedConvention>(conventions.EntityTypeAddedConventions);
        Remove<BaseTypeDiscoveryConvention, IEntityTypeAddedConvention>(conventions.EntityTypeAddedConventions);

        var builder = new ModelBuilder(conventions, context.GetService<ModelDependencies>());
        builder.Entity(projection).HasNoKey();
        var model = builder.FinalizeModel();
#if BLUETUSK_EF6_OR_GREATER
        model = context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: false);
#endif
        return model.FindEntityType(projection)!;
    }

    private static void Remove<TConvention, TItem>(IList<TItem> conventions)
        where TConvention : TItem
    {
        for (var index = conventions.Count - 1; index >= 0; index--)
        {
            if (conventions[index] is TConvention)
            {
                conventions.RemoveAt(index);
            }
        }
    }
}
#endif
