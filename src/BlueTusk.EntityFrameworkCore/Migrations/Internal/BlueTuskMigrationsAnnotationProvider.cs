using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable EF1001 // Provider implementation requires EF Core infrastructure services.

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsAnnotationProvider(
    MigrationsAnnotationProviderDependencies dependencies)
    : MigrationsAnnotationProvider(dependencies);

#pragma warning restore EF1001
