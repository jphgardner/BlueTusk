using System.ComponentModel;
using BlueTusk.Data.Internal;
#if BLUETUSK_EF10_OR_GREATER
using BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;
#endif
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
#if BLUETUSK_EF8_OR_GREATER
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
#endif
using BlueTusk.EntityFrameworkCore.Migrations.Internal;
using BlueTusk.EntityFrameworkCore.Query.Internal;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
#if BLUETUSK_EF10_OR_GREATER
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
#endif
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers BlueTusk Entity Framework Core provider services.</summary>
public static class BlueTuskServiceCollectionExtensions
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection AddEntityFrameworkBlueTusk(this IServiceCollection services)
    {
        BlueTuskGuard.NotNull(services);

        services.TryAddSingleton(ProviderServices.Instance);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IEvaluatableExpressionFilterPlugin,
                BlueTuskEvaluatableExpressionFilterPlugin>());
#pragma warning disable EF1001 // IChangeDetector is EF Core's internal change-detection service, extended by BlueTusk.
        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<LoggingDefinitions, BlueTuskLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<BlueTuskOptionsExtension>>()
            .TryAdd<IRelationalTypeMappingSource, BlueTuskTypeMappingSource>()
            .TryAdd<ISqlGenerationHelper, BlueTuskSqlGenerationHelper>()
            .TryAdd<IRelationalAnnotationProvider, BlueTuskAnnotationProvider>()
            .TryAdd<IModelValidator, BlueTuskModelValidator>()
#if BLUETUSK_EF10_OR_GREATER
            .TryAdd<IModelRuntimeInitializer, BlueTuskModelRuntimeInitializer>()
#endif
#if BLUETUSK_EF8_OR_GREATER
            .TryAdd<IConstructorBindingFactory, BlueTuskConstructorBindingFactory>()
#endif
#if BLUETUSK_EF10_OR_GREATER
            .TryAdd<IChangeDetector, BlueTuskChangeDetector>()
#endif
            .TryAdd<IModelCustomizer, BlueTuskModelCustomizer>()
            .TryAdd<IModelCacheKeyFactory, BlueTuskModelCacheKeyFactory>()
            .TryAdd<IProviderConventionSetBuilder, BlueTuskConventionSetBuilder>()
            .TryAdd<IMigrationsAnnotationProvider, BlueTuskMigrationsAnnotationProvider>()
            .TryAdd<IMigrationsModelDiffer, BlueTuskMigrationsModelDiffer>()
            .TryAdd<IMigrationsSqlGenerator, BlueTuskMigrationsSqlGenerator>()
            .TryAdd<IHistoryRepository, BlueTuskHistoryRepository>()
            .TryAdd<IMigrator, BlueTuskMigrator>()
#if BLUETUSK_EF7_OR_GREATER
            .TryAdd<IAggregateMethodCallTranslatorProvider, BlueTuskAggregateMethodCallTranslatorProvider>()
#endif
            .TryAdd<IMethodCallTranslatorProvider, BlueTuskMethodCallTranslatorProvider>()
            .TryAdd<IMemberTranslatorProvider, BlueTuskMemberTranslatorProvider>()
            .TryAdd<IRelationalParameterBasedSqlProcessorFactory, BlueTuskParameterBasedSqlProcessorFactory>()
            .TryAdd<IRelationalSqlTranslatingExpressionVisitorFactory, BlueTuskSqlTranslatingExpressionVisitorFactory>()
            .TryAdd<IQueryCompilationContextFactory, BlueTuskQueryCompilationContextFactory>()
            .TryAdd<IQueryTranslationPreprocessorFactory, BlueTuskQueryTranslationPreprocessorFactory>()
#if BLUETUSK_EF9_OR_GREATER
            .TryAdd<IQueryTranslationPostprocessorFactory, BlueTuskQueryTranslationPostprocessorFactory>()
#endif
            .TryAdd<IQuerySqlGeneratorFactory, BlueTuskQuerySqlGeneratorFactory>()
#if BLUETUSK_EF10_OR_GREATER
            .TryAdd<IShapedQueryCompilingExpressionVisitorFactory, BlueTuskShapedQueryCompilingExpressionVisitorFactory>()
            .TryAdd<IStructuralTypeMaterializerSource, BlueTuskStructuralTypeMaterializerSource>()
#endif
            .TryAdd<IQueryableMethodTranslatingExpressionVisitorFactory, BlueTuskQueryableMethodTranslatingExpressionVisitorFactory>()
            .TryAdd<IUpdateSqlGenerator, BlueTuskUpdateSqlGenerator>()
            .TryAdd<IModificationCommandBatchFactory, BlueTuskModificationCommandBatchFactory>()
            .TryAdd<IRelationalConnection, BlueTuskRelationalConnection>()
            .TryAdd<IRelationalDatabaseCreator, BlueTuskDatabaseCreator>()
            .TryAddCoreServices();
#pragma warning restore EF1001

        return services;
    }
}
