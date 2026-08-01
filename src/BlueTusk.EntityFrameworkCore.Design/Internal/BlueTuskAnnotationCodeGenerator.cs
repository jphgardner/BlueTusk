using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#pragma warning disable EF1001 // Provider design-time code consumes provider infrastructure metadata.

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

internal sealed class BlueTuskAnnotationCodeGenerator(
    AnnotationCodeGeneratorDependencies dependencies)
    : AnnotationCodeGenerator(dependencies)
{
    protected override MethodCallCodeFragment? GenerateFluentApi(
        IModel model,
        IAnnotation annotation)
    {
        if (annotation.Name == BlueTuskPropertyGraphMetadata.AnnotationName &&
            annotation.Value is string serializedDefinitions)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskPropertyGraphModelBuilderExtensions.HasBlueTuskPropertyGraphs),
                serializedDefinitions);
        }

        if (annotation.Name == BlueTuskUserDefinedTypeMetadata.AnnotationName &&
            annotation.Value is string serializedTypes)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskUserDefinedTypeModelBuilderExtensions.HasBlueTuskUserDefinedTypes),
                serializedTypes);
        }

        return base.GenerateFluentApi(model, annotation);
    }

    protected override MethodCallCodeFragment? GenerateFluentApi(
        IIndex index,
        IAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(annotation);

        return annotation.Name switch
        {
            BlueTuskIndexAnnotations.Method when annotation.Value is string value =>
                new MethodCallCodeFragment(nameof(BlueTuskIndexBuilderExtensions.UseBlueTuskIndexMethod), value),
            BlueTuskIndexAnnotations.OperatorClasses when annotation.Value is string[] values =>
                Fragment(nameof(BlueTuskIndexBuilderExtensions.UseBlueTuskOperatorClass), values),
            BlueTuskIndexAnnotations.Collations when annotation.Value is string[] values =>
                Fragment(nameof(BlueTuskIndexBuilderExtensions.UseBlueTuskCollation), values),
            BlueTuskIndexAnnotations.NullSortOrders when annotation.Value is int[] values =>
                new MethodCallCodeFragment(
                    nameof(BlueTuskIndexBuilderExtensions.HasBlueTuskNullSortOrder),
                    values.Select(value => (object)(BlueTuskIndexNullSortOrder)value).ToArray()),
            BlueTuskIndexAnnotations.IncludeProperties when annotation.Value is string[] values =>
                Fragment(
                    nameof(BlueTuskIndexBuilderExtensions.IncludeProperties),
                    MapIncludedPropertyNames(index, values)),
            BlueTuskIndexAnnotations.IsConcurrent when annotation.Value is bool value =>
                new MethodCallCodeFragment(nameof(BlueTuskIndexBuilderExtensions.IsBlueTuskConcurrent), value),
            BlueTuskIndexAnnotations.NullsDistinct when annotation.Value is bool value =>
                new MethodCallCodeFragment(nameof(BlueTuskIndexBuilderExtensions.HasBlueTuskNullsDistinct), value),
            BlueTuskIndexAnnotations.Expressions when annotation.Value is string[] values =>
                Fragment(
                    nameof(BlueTuskIndexBuilderExtensions.HasBlueTuskIndexExpressions),
                    values.Select(value => string.IsNullOrEmpty(value) ? null : value).ToArray()),
            _ => base.GenerateFluentApi(index, annotation),
        };
    }

    protected override MethodCallCodeFragment? GenerateFluentApi(
        IEntityType entityType,
        IAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(annotation);

        if (annotation.Name == BlueTuskPartitionMetadata.AnnotationName &&
            annotation.Value is string serializedDefinition)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskPartitioningBuilderExtensions.HasBlueTuskPartitioning),
                serializedDefinition);
        }

        if (annotation.Name == BlueTuskRowLevelSecurityMetadata.AnnotationName &&
            annotation.Value is string serializedRowLevelSecurity)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskRowLevelSecurityBuilderExtensions.HasBlueTuskRowLevelSecurity),
                serializedRowLevelSecurity);
        }

        if (annotation.Name == BlueTuskTableInheritanceMetadata.AnnotationName &&
            annotation.Value is string serializedInheritance)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskTableInheritanceBuilderExtensions.HasBlueTuskTableInheritance),
                serializedInheritance);
        }

        return base.GenerateFluentApi(entityType, annotation);
    }

    private static MethodCallCodeFragment Fragment(string method, string?[] arguments) =>
        new(method, arguments.Cast<object>().ToArray());

    private static string[] MapIncludedPropertyNames(IIndex index, IReadOnlyList<string> values)
    {
        var tableName = index.DeclaringEntityType.GetTableName();
        if (tableName is null)
        {
            return values.ToArray();
        }

        var storeObject = StoreObjectIdentifier.Table(tableName, index.DeclaringEntityType.GetSchema());
        return values.Select(
                value => index.DeclaringEntityType.FindProperty(value)?.Name
                    ?? index.DeclaringEntityType.GetProperties()
                        .FirstOrDefault(property => property.GetColumnName(storeObject) == value)?.Name
                    ?? value)
            .ToArray();
    }
}

#pragma warning restore EF1001
