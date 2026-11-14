using Microsoft.EntityFrameworkCore.Query;
#if !BLUETUSK_EF6_OR_GREATER
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;
#endif

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskSqlTranslatingExpressionVisitorFactory(
    RelationalSqlTranslatingExpressionVisitorDependencies dependencies
#if !BLUETUSK_EF7_OR_GREATER
    , IEnumerable<IBlueTuskLegacyAggregateTranslatorPlugin> aggregatePlugins
#endif
#if !BLUETUSK_EF6_OR_GREATER
    , RelationalMemberTranslatorProviderDependencies memberDependencies,
    RelationalMethodCallTranslatorProviderDependencies methodDependencies,
    ISqlGenerationHelper sqlGenerationHelper
#endif
    )
    : IRelationalSqlTranslatingExpressionVisitorFactory
{
    public RelationalSqlTranslatingExpressionVisitor Create(
        QueryCompilationContext queryCompilationContext,
        QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
    {
        var queryDependencies = dependencies;
#if !BLUETUSK_EF6_OR_GREATER
        // EF5 registers its translator providers as singletons. Bind catalogue access to the query's context.
        if (queryCompilationContext.ContextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource?.TypeRegistry
            is { } registry)
        {
            if (dependencies.MemberTranslatorProvider is BlueTuskMemberTranslatorProvider)
            {
                queryDependencies = queryDependencies.With(new BlueTuskMemberTranslatorProvider(
                    memberDependencies, dependencies.TypeMappingSource, sqlGenerationHelper, registry));
            }

            if (dependencies.MethodCallTranslatorProvider is BlueTuskMethodCallTranslatorProvider)
            {
                queryDependencies = queryDependencies.With(new BlueTuskMethodCallTranslatorProvider(
                    methodDependencies, sqlGenerationHelper, registry));
            }
        }
#endif
        return new BlueTuskSqlTranslatingExpressionVisitor(
            queryDependencies,
            queryCompilationContext,
            queryableMethodTranslatingExpressionVisitor
#if !BLUETUSK_EF7_OR_GREATER
            , aggregatePlugins.SelectMany(plugin => plugin.Translators)
#endif
            );
    }
}
