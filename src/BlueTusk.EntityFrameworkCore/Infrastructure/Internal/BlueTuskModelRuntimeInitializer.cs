#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Runtime.CompilerServices;
using BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

// EF Core 10 snapshot factories substitute default values for shadow properties of complex types and add to fixed-size
// complex collection snapshots. Structural types that use either shape get BlueTusk snapshot factories instead; every
// other type keeps EF Core's lazily created factories.
internal sealed class BlueTuskModelRuntimeInitializer(
    ModelRuntimeInitializerDependencies dependencies,
    RelationalModelRuntimeInitializerDependencies relationalDependencies)
    : RelationalModelRuntimeInitializer(dependencies, relationalDependencies)
{
    private const string SnapshotFactoriesAnnotation = "BlueTusk:SnapshotFactories";

    public override IModel Initialize(
        IModel model,
        bool designTime = true,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation>? validationLogger = null)
    {
        var initialized = base.Initialize(model, designTime, validationLogger);

        // Model sources initialize the design-time model and read the runtime model from its read-only annotation.
        var runtimeModel = initialized as RuntimeModel
            ?? initialized.FindRuntimeAnnotationValue(CoreAnnotationNames.ReadOnlyModel) as RuntimeModel;
        if (RuntimeFeature.IsDynamicCodeSupported && runtimeModel is not null)
        {
            _ = runtimeModel.GetOrAddRuntimeAnnotationValue(
                SnapshotFactoriesAnnotation,
                static model =>
                {
                    foreach (var entityType in ((IModel)model!).GetEntityTypes().Cast<RuntimeEntityType>())
                    {
                        Install(entityType);
                        if (BlueTuskValueTypeCollectionAccessors.HasValueTypeCollection(entityType))
                        {
                            BlueTuskValueTypeCollectionAccessors.Install(entityType);
                        }
                    }

                    return true;
                },
                runtimeModel);
        }

        return initialized;
    }

    private static void Install(RuntimeEntityType entityType)
    {
        if (RequiresSnapshotFactories(entityType))
        {
            InstallStructuralFactories(entityType);
            entityType.SetRelationshipSnapshotFactory(BlueTuskRelationshipSnapshotFactoryFactory.Instance.Create(entityType));
        }

        foreach (var complexType in GetCollectionElementTypes(entityType))
        {
            if (RequiresSnapshotFactories(complexType))
            {
                InstallStructuralFactories(complexType);
            }
        }
    }

    private static void InstallStructuralFactories(RuntimeTypeBase structuralType)
    {
        structuralType.SetOriginalValuesFactory(BlueTuskOriginalValuesFactoryFactory.Instance.Create(structuralType));
        structuralType.SetStoreGeneratedValuesFactory(BlueTuskStoreGeneratedValuesFactoryFactory.Instance.CreateEmpty(structuralType));
        structuralType.SetTemporaryValuesFactory(BlueTuskTemporaryValuesFactoryFactory.Instance.Create(structuralType));
        structuralType.SetShadowValuesFactory(BlueTuskShadowValuesFactoryFactory.Instance.Create(structuralType));
        structuralType.SetEmptyShadowValuesFactory(BlueTuskEmptyShadowValuesFactoryFactory.Instance.CreateEmpty(structuralType));
    }

    private static IEnumerable<RuntimeComplexType> GetCollectionElementTypes(ITypeBase structuralType)
    {
        foreach (var complexProperty in structuralType.GetComplexProperties())
        {
            if (complexProperty.IsCollection)
            {
                yield return (RuntimeComplexType)complexProperty.ComplexType;
            }

            foreach (var nested in GetCollectionElementTypes(complexProperty.ComplexType))
            {
                yield return nested;
            }
        }
    }

    // The snapshot of a structural type covers its own properties and, recursively, those of non-collection complex
    // properties; complex collection elements have their own snapshots.
    private static bool RequiresSnapshotFactories(ITypeBase structuralType)
    {
        foreach (var complexProperty in structuralType.GetComplexProperties())
        {
            if (complexProperty.IsCollection)
            {
                if (complexProperty.ClrType.IsArray)
                {
                    return true;
                }

                continue;
            }

            var complexType = complexProperty.ComplexType;
            var discriminator = complexType.FindDiscriminatorProperty();
            if (complexType.GetProperties().Any(p => p.IsShadowProperty() && p != discriminator)
                || RequiresSnapshotFactories(complexType))
            {
                return true;
            }
        }

        return false;
    }
}
