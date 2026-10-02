using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskModelValidator(
    ModelValidatorDependencies dependencies,
    RelationalModelValidatorDependencies relationalDependencies)
    : RelationalModelValidator(dependencies, relationalDependencies)
{
    // EF Core 10 rejects non-discriminator shadow properties on reference complex types (dotnet/efcore#35613) as the
    // last check of ModelValidator.ValidatePropertyMapping. BlueTusk supports them end to end, so only that exact
    // rejection is accepted here; every earlier core check has already passed when it is thrown. The relational
    // checks that RelationalModelValidator runs after the core method are then reproduced from EF Core 10.0.11.
    protected override void ValidatePropertyMapping(
        IConventionComplexProperty complexProperty,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        try
        {
            base.ValidatePropertyMapping(complexProperty, logger);
            return;
        }
        catch (InvalidOperationException exception) when (IsSupportedComplexTypeShadowProperty(complexProperty, exception))
        {
        }

        ValidateRelationalComplexPropertyMapping(complexProperty);
    }

    private static bool IsSupportedComplexTypeShadowProperty(
        IConventionComplexProperty complexProperty,
        InvalidOperationException exception)
    {
        var complexType = complexProperty.ComplexType;
        if (complexType.ClrType.IsValueType)
        {
            return false;
        }

        var shadowProperty = complexType.GetDeclaredProperties()
            .FirstOrDefault(p => p.IsShadowProperty() && p != complexType.FindDiscriminatorProperty());
        return shadowProperty is not null
            && exception.Message == CoreStrings.ComplexTypeShadowProperty(complexType.DisplayName(), shadowProperty.Name);
    }

    private static void ValidateRelationalComplexPropertyMapping(IConventionComplexProperty complexProperty)
    {
        if (complexProperty.IsCollection && !complexProperty.ComplexType.IsMappedToJson())
        {
            throw new InvalidOperationException(
                RelationalStrings.ComplexCollectionNotMappedToJson(
                    complexProperty.DeclaringType.DisplayName(), complexProperty.Name));
        }

        if (!complexProperty.ComplexType.IsMappedToJson()
            && complexProperty.IsNullable
            && complexProperty.ComplexType.GetProperties().All(m => m.IsNullable))
        {
            throw new InvalidOperationException(
                RelationalStrings.ComplexPropertyOptionalTableSharing(
                    complexProperty.ComplexType.DisplayName(), complexProperty.Name));
        }

        if (complexProperty.GetJsonPropertyName() != null)
        {
            if (complexProperty.ComplexType.FindAnnotation(RelationalAnnotationNames.ContainerColumnName)?.Value is string columnName)
            {
                throw new InvalidOperationException(
                    RelationalStrings.ComplexPropertyBothJsonColumnAndJsonPropertyName(
                        $"{complexProperty.DeclaringType.DisplayName()}.{complexProperty.Name}",
                        columnName,
                        complexProperty.GetJsonPropertyName()));
            }

            if (!complexProperty.DeclaringType.IsMappedToJson())
            {
                throw new InvalidOperationException(
                    RelationalStrings.ComplexPropertyJsonPropertyNameWithoutJsonMapping(
                        $"{complexProperty.DeclaringType.DisplayName()}.{complexProperty.Name}"));
            }
        }

        if (complexProperty.ComplexType.IsMappedToJson())
        {
            if (!complexProperty.DeclaringType.IsMappedToJson()
                && complexProperty.DeclaringType is IComplexType)
            {
                throw new InvalidOperationException(
                    RelationalStrings.NestedComplexPropertyJsonWithTableSharing(
                        $"{complexProperty.DeclaringType.DisplayName()}.{complexProperty.Name}",
                        complexProperty.DeclaringType.DisplayName()));
            }

            ValidateJsonProperties(complexProperty.ComplexType);
        }
    }

    private static void ValidateJsonProperties(IConventionTypeBase typeBase)
    {
        var jsonPropertyNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in typeBase.GetProperties())
        {
            var jsonPropertyName = property.GetJsonPropertyName();
            if (string.IsNullOrEmpty(jsonPropertyName))
            {
                continue;
            }

            var columnNameAnnotation = property.FindAnnotation(RelationalAnnotationNames.ColumnName);
            if (columnNameAnnotation != null && !string.IsNullOrEmpty((string?)columnNameAnnotation.Value))
            {
                throw new InvalidOperationException(
                    RelationalStrings.PropertyBothColumnNameAndJsonPropertyName(
                        $"{typeBase.DisplayName()}.{property.Name}",
                        (string)columnNameAnnotation.Value,
                        jsonPropertyName));
            }

            if (property.TryGetDefaultValue(out _))
            {
                throw new InvalidOperationException(
                    RelationalStrings.JsonEntityWithDefaultValueSetOnItsProperty(typeBase.DisplayName(), property.Name));
            }

            CheckUniqueness(jsonPropertyName, property.Name, typeBase, jsonPropertyNames);

            if (property.IsConcurrencyToken)
            {
                throw new InvalidOperationException(
                    RelationalStrings.ConcurrencyTokenOnJsonMappedProperty(property.Name, typeBase.DisplayName()));
            }
        }

        foreach (var complexProperty in typeBase.GetComplexProperties())
        {
            var jsonPropertyName = complexProperty.GetJsonPropertyName();
            if (jsonPropertyName != null)
            {
                CheckUniqueness(jsonPropertyName, complexProperty.Name, typeBase, jsonPropertyNames);
            }
        }
    }

    private static void CheckUniqueness(
        string jsonPropertyName,
        string propertyName,
        IReadOnlyTypeBase structuralType,
        Dictionary<string, string> jsonPropertyNames)
    {
        if (jsonPropertyNames.TryGetValue(jsonPropertyName, out var existingProperty))
        {
            throw new InvalidOperationException(
                RelationalStrings.JsonObjectWithMultiplePropertiesMappedToSameJsonProperty(
                    existingProperty, propertyName, structuralType.DisplayName(), jsonPropertyName));
        }

        jsonPropertyNames[jsonPropertyName] = propertyName;
    }
}
