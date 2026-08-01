using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable EF1001 // Provider implementation requires EF Core infrastructure services.

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsAnnotationProvider(
    MigrationsAnnotationProviderDependencies dependencies)
    : MigrationsAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> ForRemove(ITableIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return base.ForRemove(index)
            .Concat(index.GetAnnotations()
                .Where(annotation => annotation.Name.StartsWith(BlueTuskIndexAnnotations.Prefix, StringComparison.Ordinal)));
    }
}

#pragma warning restore EF1001
