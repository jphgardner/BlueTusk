using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Internal;
using BlueTusk.EntityFrameworkCore.Query.Internal;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers BlueTusk Entity Framework Core provider services.</summary>
public static class BlueTuskServiceCollectionExtensions
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection AddEntityFrameworkBlueTusk(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<LoggingDefinitions, BlueTuskLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<BlueTuskOptionsExtension>>()
            .TryAdd<IRelationalTypeMappingSource, BlueTuskTypeMappingSource>()
            .TryAdd<ISqlGenerationHelper, BlueTuskSqlGenerationHelper>()
            .TryAdd<IRelationalAnnotationProvider, BlueTuskAnnotationProvider>()
            .TryAdd<IModelValidator, BlueTuskModelValidator>()
            .TryAdd<IProviderConventionSetBuilder, BlueTuskConventionSetBuilder>()
            .TryAdd<IMigrationsAnnotationProvider, BlueTuskMigrationsAnnotationProvider>()
            .TryAdd<IMigrationsModelDiffer, BlueTuskMigrationsModelDiffer>()
            .TryAdd<IMigrationsSqlGenerator, BlueTuskMigrationsSqlGenerator>()
            .TryAdd<IHistoryRepository, BlueTuskHistoryRepository>()
            .TryAdd<IMethodCallTranslatorProvider, BlueTuskMethodCallTranslatorProvider>()
            .TryAdd<IMemberTranslatorProvider, BlueTuskMemberTranslatorProvider>()
            .TryAdd<IRelationalParameterBasedSqlProcessorFactory, BlueTuskParameterBasedSqlProcessorFactory>()
            .TryAdd<IQuerySqlGeneratorFactory, BlueTuskQuerySqlGeneratorFactory>()
            .TryAdd<IUpdateSqlGenerator, BlueTuskUpdateSqlGenerator>()
            .TryAdd<IModificationCommandBatchFactory, BlueTuskModificationCommandBatchFactory>()
            .TryAdd<IRelationalConnection, BlueTuskRelationalConnection>()
            .TryAdd<IRelationalDatabaseCreator, BlueTuskDatabaseCreator>()
            .TryAddCoreServices();

        return services;
    }
}
