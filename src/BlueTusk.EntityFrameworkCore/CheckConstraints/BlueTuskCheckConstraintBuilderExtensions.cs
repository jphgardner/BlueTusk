using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL-specific CHECK-constraint configuration.</summary>
public static class BlueTuskCheckConstraintBuilderExtensions
{
    /// <summary>
    /// Adds the constraint without scanning existing rows. PostgreSQL still enforces the constraint for new or changed rows.
    /// </summary>
    public static CheckConstraintBuilder IsNotValid(
        this CheckConstraintBuilder checkConstraintBuilder,
        bool notValid = true)
    {
        ArgumentNullException.ThrowIfNull(checkConstraintBuilder);
        SetOrRemove(checkConstraintBuilder.Metadata, BlueTuskCheckConstraintMetadata.NotValidAnnotationName, notValid);
        return checkConstraintBuilder;
    }

    /// <summary>Prevents the CHECK constraint from being inherited by child tables.</summary>
    public static CheckConstraintBuilder IsNoInherit(
        this CheckConstraintBuilder checkConstraintBuilder,
        bool noInherit = true)
    {
        ArgumentNullException.ThrowIfNull(checkConstraintBuilder);
        SetOrRemove(checkConstraintBuilder.Metadata, BlueTuskCheckConstraintMetadata.NoInheritAnnotationName, noInherit);
        return checkConstraintBuilder;
    }

    /// <summary>
    /// Disables enforcement of the CHECK constraint. This requires PostgreSQL 18 or later.
    /// </summary>
    public static CheckConstraintBuilder IsNotEnforced(
        this CheckConstraintBuilder checkConstraintBuilder,
        bool notEnforced = true)
    {
        ArgumentNullException.ThrowIfNull(checkConstraintBuilder);
        SetOrRemove(
            checkConstraintBuilder.Metadata,
            BlueTuskCheckConstraintMetadata.NotEnforcedAnnotationName,
            notEnforced);
        return checkConstraintBuilder;
    }

    /// <summary>Returns whether the CHECK constraint is configured as PostgreSQL NOT VALID.</summary>
    public static bool IsNotValid(this IReadOnlyCheckConstraint checkConstraint)
    {
        ArgumentNullException.ThrowIfNull(checkConstraint);
        return BlueTuskCheckConstraintMetadata.IsNotValid(checkConstraint);
    }

    /// <summary>Returns whether the CHECK constraint is configured as PostgreSQL NO INHERIT.</summary>
    public static bool IsNoInherit(this IReadOnlyCheckConstraint checkConstraint)
    {
        ArgumentNullException.ThrowIfNull(checkConstraint);
        return BlueTuskCheckConstraintMetadata.HasNoInherit(checkConstraint);
    }

    /// <summary>Returns whether the CHECK constraint is configured as PostgreSQL NOT ENFORCED.</summary>
    public static bool IsNotEnforced(this IReadOnlyCheckConstraint checkConstraint)
    {
        ArgumentNullException.ThrowIfNull(checkConstraint);
        return BlueTuskCheckConstraintMetadata.IsNotEnforced(checkConstraint);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasCheckConstraints(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        foreach (var definition in BlueTuskCheckConstraintMetadata.Deserialize(serializedDefinitions))
        {
            var constraint = entityBuilder.Metadata.AddCheckConstraint(definition.Name, definition.Sql);
#pragma warning disable EF1001 // Scaffolding bridge must attach provider annotations to EF's CHECK metadata.
            var checkConstraint = new CheckConstraintBuilder(constraint);
#pragma warning restore EF1001
            checkConstraint.IsNotValid(definition.IsNotValid);
            checkConstraint.IsNoInherit(definition.NoInherit);
            checkConstraint.IsNotEnforced(definition.IsNotEnforced);
        }

        return entityBuilder;
    }

    private static void SetOrRemove(IMutableCheckConstraint constraint, string annotationName, bool enabled)
    {
        if (enabled)
        {
            constraint.SetAnnotation(annotationName, true);
        }
        else
        {
            constraint.RemoveAnnotation(annotationName);
        }
    }
}
