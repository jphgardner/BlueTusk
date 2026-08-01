using BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
    : RelationalAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var annotation in base.For(table, designTime))
        {
            yield return annotation;
        }

        if (BlueTuskPartitionMetadata.GetTableDefinition(table) is { } definition)
        {
            yield return new Annotation(
                BlueTuskPartitionMetadata.AnnotationName,
                BlueTuskPartitionMetadata.Serialize(definition));
        }

        if (BlueTuskRowLevelSecurityMetadata.GetTableDefinition(table) is { } rowLevelSecurity)
        {
            yield return new Annotation(
                BlueTuskRowLevelSecurityMetadata.AnnotationName,
                BlueTuskRowLevelSecurityMetadata.Serialize(rowLevelSecurity));
        }

        if (BlueTuskTableInheritanceMetadata.GetTableDefinition(table) is { } inheritance)
        {
            yield return new Annotation(
                BlueTuskTableInheritanceMetadata.AnnotationName,
                BlueTuskTableInheritanceMetadata.Serialize(inheritance));
        }

        if (BlueTuskForeignDataMetadata.GetTableDefinition(table) is { } foreignTable)
        {
            yield return new Annotation(
                BlueTuskForeignDataMetadata.ForeignTableAnnotationName,
                BlueTuskForeignDataMetadata.Serialize(foreignTable));
        }
    }

    public override IEnumerable<IAnnotation> For(ITableIndex index, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(index);

        foreach (var annotation in base.For(index, designTime))
        {
            yield return annotation;
        }

        var mappedIndex = index.MappedIndexes.FirstOrDefault();
        if (mappedIndex is null)
        {
            yield break;
        }

        foreach (var annotation in mappedIndex.GetAnnotations()
                     .Where(annotation => annotation.Name.StartsWith(BlueTuskIndexAnnotations.Prefix, StringComparison.Ordinal)))
        {
            if (annotation.Name == BlueTuskIndexAnnotations.IncludeProperties &&
                annotation.Value is string[] propertyNames)
            {
                var storeObject = StoreObjectIdentifier.Table(index.Table.Name, index.Table.Schema);
                var columnNames = propertyNames.Select(
                        propertyName => mappedIndex.DeclaringEntityType.FindProperty(propertyName)
                            ?.GetColumnName(storeObject)
                            ?? throw new InvalidOperationException(
                                $"Included index property '{propertyName}' is not mapped to table '{index.Table.Schema}.{index.Table.Name}'."))
                    .ToArray();
                yield return new Annotation(annotation.Name, columnNames);
            }
            else
            {
                yield return annotation;
            }
        }
    }

    public override IEnumerable<IAnnotation> For(IColumn column, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(column);
        foreach (var annotation in base.For(column, designTime))
        {
            yield return annotation;
        }

        var mappedProperties = column.PropertyMappings.Select(mapping => mapping.Property).ToArray();
        var explicitModes = mappedProperties
            .Select(property => property.FindAnnotation(BlueTuskValueGenerationAnnotations.IdentityGeneration)?.Value)
            .Where(value => value is not null)
            .Select(value => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        if (explicitModes.Length > 1)
        {
            throw new InvalidOperationException(
                $"Column '{column.Table.Schema}.{column.Table.Name}.{column.Name}' has conflicting identity modes.");
        }

        if (explicitModes.Length == 1)
        {
            yield return new Annotation(
                BlueTuskValueGenerationAnnotations.IdentityGeneration,
                explicitModes[0]);
            yield break;
        }

        if (mappedProperties.Any(property =>
                property.ValueGenerated == ValueGenerated.OnAdd &&
                property.IsPrimaryKey() &&
                IsIdentityType(property.ClrType)))
        {
            yield return new Annotation(
                BlueTuskValueGenerationAnnotations.IdentityGeneration,
                (int)BlueTuskIdentityGeneration.ByDefault);
        }
    }

    public override IEnumerable<IAnnotation> For(ICheckConstraint constraint, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        foreach (var annotation in base.For(constraint, designTime))
        {
            yield return annotation;
        }

        foreach (var annotation in constraint.GetAnnotations()
                     .Where(annotation => annotation.Name.StartsWith(
                         BlueTuskCheckConstraintMetadata.Prefix,
                         StringComparison.Ordinal)))
        {
            yield return annotation;
        }
    }

    private static bool IsIdentityType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(short) || type == typeof(int) || type == typeof(long);
    }
}
