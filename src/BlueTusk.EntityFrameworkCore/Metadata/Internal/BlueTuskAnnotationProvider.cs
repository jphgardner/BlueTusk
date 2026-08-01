using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
    : RelationalAnnotationProvider(dependencies)
{
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
}
