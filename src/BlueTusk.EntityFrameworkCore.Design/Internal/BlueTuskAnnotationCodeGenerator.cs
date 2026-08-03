using BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using BlueTusk.EntityFrameworkCore.Views.Internal;
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
        if (annotation.Name == BlueTuskCollationMetadata.AnnotationName &&
            annotation.Value is string serializedCollations)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskCollationModelBuilderExtensions.HasCollations),
                serializedCollations);
        }

        if (annotation.Name == BlueTuskExtensionMetadata.AnnotationName &&
            annotation.Value is string serializedExtensions)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskExtensionModelBuilderExtensions.HasExtensions),
                serializedExtensions);
        }

        if (annotation.Name == BlueTuskForeignDataMetadata.AnnotationName &&
            annotation.Value is string serializedForeignData)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskForeignDataModelBuilderExtensions.HasForeignData),
                serializedForeignData);
        }

        if (annotation.Name == BlueTuskEventTriggerMetadata.AnnotationName &&
            annotation.Value is string serializedEventTriggers)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskEventTriggerModelBuilderExtensions.HasEventTriggers),
                serializedEventTriggers);
        }

        if (annotation.Name == BlueTuskTablespaceMetadata.AnnotationName &&
            annotation.Value is string serializedTablespaces)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskTablespaceModelBuilderExtensions.HasTablespaces),
                serializedTablespaces);
        }

        if (annotation.Name == BlueTuskPropertyGraphMetadata.AnnotationName &&
            annotation.Value is string serializedDefinitions)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskPropertyGraphModelBuilderExtensions.HasPropertyGraphs),
                serializedDefinitions);
        }

        if (annotation.Name == BlueTuskUserDefinedTypeMetadata.AnnotationName &&
            annotation.Value is string serializedTypes)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskUserDefinedTypeModelBuilderExtensions.HasUserDefinedTypes),
                serializedTypes);
        }

        if (annotation.Name == BlueTuskRoutineMetadata.AnnotationName &&
            annotation.Value is string serializedRoutines)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskRoutineModelBuilderExtensions.HasRoutines),
                serializedRoutines);
        }

        if (annotation.Name == BlueTuskViewMetadata.AnnotationName &&
            annotation.Value is string serializedViews)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskViewModelBuilderExtensions.HasViews),
                serializedViews);
        }

        if (annotation.Name == BlueTuskPublicationMetadata.AnnotationName &&
            annotation.Value is string serializedPublications)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskPublicationModelBuilderExtensions.HasPublications),
                serializedPublications);
        }

        if (annotation.Name == BlueTuskSubscriptionMetadata.AnnotationName &&
            annotation.Value is string serializedSubscriptions)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskSubscriptionModelBuilderExtensions.HasSubscriptions),
                serializedSubscriptions);
        }

        if (annotation.Name == BlueTuskSchemaProgramMetadata.AnnotationName &&
            annotation.Value is string serializedSchemaPrograms)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskSchemaProgramModelBuilderExtensions.HasSchemaPrograms),
                serializedSchemaPrograms);
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
                new MethodCallCodeFragment(nameof(BlueTuskIndexBuilderExtensions.UseIndexMethod), value),
            BlueTuskIndexAnnotations.OperatorClasses when annotation.Value is string[] values =>
                Fragment(nameof(BlueTuskIndexBuilderExtensions.UseOperatorClass), values),
            BlueTuskIndexAnnotations.Collations when annotation.Value is string[] values =>
                Fragment(nameof(BlueTuskIndexBuilderExtensions.UseCollation), values),
            BlueTuskIndexAnnotations.NullSortOrders when annotation.Value is int[] values =>
                new MethodCallCodeFragment(
                    nameof(BlueTuskIndexBuilderExtensions.HasNullSortOrder),
                    values.Select(value => (object)(BlueTuskIndexNullSortOrder)value).ToArray()),
            BlueTuskIndexAnnotations.IncludeProperties when annotation.Value is string[] values =>
                Fragment(
                    nameof(BlueTuskIndexBuilderExtensions.IncludeProperties),
                    MapIncludedPropertyNames(index, values)),
            BlueTuskIndexAnnotations.IsConcurrent when annotation.Value is bool value =>
                new MethodCallCodeFragment(nameof(BlueTuskIndexBuilderExtensions.IsConcurrent), value),
            BlueTuskIndexAnnotations.NullsDistinct when annotation.Value is bool value =>
                new MethodCallCodeFragment(nameof(BlueTuskIndexBuilderExtensions.HasNullsDistinct), value),
            BlueTuskIndexAnnotations.Expressions when annotation.Value is string[] values =>
                Fragment(
                    nameof(BlueTuskIndexBuilderExtensions.HasIndexExpressions),
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
                nameof(BlueTuskPartitioningBuilderExtensions.HasPartitioning),
                serializedDefinition);
        }

        if (annotation.Name == BlueTuskForeignDataMetadata.ForeignTableAnnotationName &&
            annotation.Value is string serializedForeignTable)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskForeignDataModelBuilderExtensions.HasForeignTableDefinition),
                serializedForeignTable);
        }

        if (annotation.Name == BlueTuskRowLevelSecurityMetadata.AnnotationName &&
            annotation.Value is string serializedRowLevelSecurity)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskRowLevelSecurityBuilderExtensions.HasRowLevelSecurity),
                serializedRowLevelSecurity);
        }

        if (annotation.Name == BlueTuskExclusionConstraintMetadata.AnnotationName &&
            annotation.Value is string serializedExclusionConstraints)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskExclusionConstraintBuilderExtensions.HasExclusionConstraints),
                serializedExclusionConstraints);
        }

        if (annotation.Name == BlueTuskExpressionIndexMetadata.AnnotationName &&
            annotation.Value is string serializedExpressionIndexes)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskExpressionIndexBuilderExtensions.HasExpressionIndexes),
                serializedExpressionIndexes);
        }

        if (annotation.Name == BlueTuskCheckConstraintMetadata.ScaffoldAnnotationName &&
            annotation.Value is string serializedCheckConstraints)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskCheckConstraintBuilderExtensions.HasCheckConstraints),
                serializedCheckConstraints);
        }

        if (annotation.Name == BlueTuskTriggerMetadata.AnnotationName &&
            annotation.Value is string serializedTriggers)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskTriggerBuilderExtensions.HasTriggers),
                serializedTriggers);
        }

        if (annotation.Name == BlueTuskRuleMetadata.AnnotationName &&
            annotation.Value is string serializedRules)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskRuleBuilderExtensions.HasRules),
                serializedRules);
        }

        if (annotation.Name == BlueTuskTableInheritanceMetadata.AnnotationName &&
            annotation.Value is string serializedInheritance)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskTableInheritanceBuilderExtensions.HasTableInheritance),
                serializedInheritance);
        }

        return base.GenerateFluentApi(entityType, annotation);
    }

    protected override MethodCallCodeFragment? GenerateFluentApi(
        ICheckConstraint checkConstraint,
        IAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(checkConstraint);
        ArgumentNullException.ThrowIfNull(annotation);

        return annotation.Name switch
        {
            BlueTuskCheckConstraintMetadata.NotValidAnnotationName when annotation.Value is bool value =>
                new MethodCallCodeFragment(
                    nameof(BlueTuskCheckConstraintBuilderExtensions.IsNotValid),
                    value),
            BlueTuskCheckConstraintMetadata.NoInheritAnnotationName when annotation.Value is bool value =>
                new MethodCallCodeFragment(
                    nameof(BlueTuskCheckConstraintBuilderExtensions.IsNoInherit),
                    value),
            BlueTuskCheckConstraintMetadata.NotEnforcedAnnotationName when annotation.Value is bool value =>
                new MethodCallCodeFragment(
                    nameof(BlueTuskCheckConstraintBuilderExtensions.IsNotEnforced),
                    value),
            _ => base.GenerateFluentApi(checkConstraint, annotation),
        };
    }

    protected override MethodCallCodeFragment? GenerateFluentApi(
        IProperty property,
        IAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(annotation);

        if (annotation.Name == BlueTuskValueGenerationAnnotations.IdentityGeneration &&
            annotation.Value is not null)
        {
            var generation = (BlueTuskIdentityGeneration)Convert.ToInt32(
                annotation.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            return new MethodCallCodeFragment(
                nameof(BlueTuskPropertyBuilderExtensions.UseIdentityColumn),
                generation);
        }

        return base.GenerateFluentApi(property, annotation);
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
