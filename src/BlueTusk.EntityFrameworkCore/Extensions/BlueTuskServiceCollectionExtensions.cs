using System.ComponentModel;
using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Internal;
using BlueTusk.EntityFrameworkCore.Query.Internal;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
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
        ArgumentNullException.ThrowIfNull(services);

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
            .TryAdd<IModelRuntimeInitializer, BlueTuskModelRuntimeInitializer>()
            .TryAdd<IConstructorBindingFactory, BlueTuskConstructorBindingFactory>()
            .TryAdd<IChangeDetector, BlueTuskChangeDetector>()
            .TryAdd<IProviderConventionSetBuilder, BlueTuskConventionSetBuilder>()
            .TryAdd<IMigrationsAnnotationProvider, BlueTuskMigrationsAnnotationProvider>()
            .TryAdd<IMigrationsModelDiffer, BlueTuskMigrationsModelDiffer>()
            .TryAdd<IMigrationsSqlGenerator, BlueTuskMigrationsSqlGenerator>()
            .TryAdd<IHistoryRepository, BlueTuskHistoryRepository>()
            .TryAdd<IMigrator, BlueTuskMigrator>()
            .TryAdd<IAggregateMethodCallTranslatorProvider, BlueTuskAggregateMethodCallTranslatorProvider>()
            .TryAdd<IMethodCallTranslatorProvider, BlueTuskMethodCallTranslatorProvider>()
            .TryAdd<IMemberTranslatorProvider, BlueTuskMemberTranslatorProvider>()
            .TryAdd<IRelationalParameterBasedSqlProcessorFactory, BlueTuskParameterBasedSqlProcessorFactory>()
            .TryAdd<IRelationalSqlTranslatingExpressionVisitorFactory, BlueTuskSqlTranslatingExpressionVisitorFactory>()
            .TryAdd<IQueryCompilationContextFactory, BlueTuskQueryCompilationContextFactory>()
            .TryAdd<IQueryTranslationPreprocessorFactory, BlueTuskQueryTranslationPreprocessorFactory>()
            .TryAdd<IQueryTranslationPostprocessorFactory, BlueTuskQueryTranslationPostprocessorFactory>()
            .TryAdd<IQuerySqlGeneratorFactory, BlueTuskQuerySqlGeneratorFactory>()
            .TryAdd<IShapedQueryCompilingExpressionVisitorFactory, BlueTuskShapedQueryCompilingExpressionVisitorFactory>()
            .TryAdd<IStructuralTypeMaterializerSource, BlueTuskStructuralTypeMaterializerSource>()
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
