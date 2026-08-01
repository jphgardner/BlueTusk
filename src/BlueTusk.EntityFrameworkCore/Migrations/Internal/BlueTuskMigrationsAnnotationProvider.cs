using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable EF1001 // Provider implementation requires EF Core infrastructure services.

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsAnnotationProvider(
    MigrationsAnnotationProviderDependencies dependencies)
    : MigrationsAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> ForRemove(ITable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return base.ForRemove(table)
            .Concat(table.GetAnnotations()
                .Where(annotation => annotation.Name is
                    BlueTuskForeignDataMetadata.ForeignTableAnnotationName or
                    BlueTuskPartitionMetadata.AnnotationName or
                    BlueTuskRowLevelSecurityMetadata.AnnotationName or
                    BlueTuskTableInheritanceMetadata.AnnotationName));
    }

    public override IEnumerable<IAnnotation> ForRemove(ITableIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return base.ForRemove(index)
            .Concat(index.GetAnnotations()
                .Where(annotation => annotation.Name.StartsWith(BlueTuskIndexAnnotations.Prefix, StringComparison.Ordinal)));
    }
}

#pragma warning restore EF1001
