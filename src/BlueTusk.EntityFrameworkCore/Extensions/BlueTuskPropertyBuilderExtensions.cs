using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Determines how PostgreSQL handles explicit values for an identity column.</summary>
public enum BlueTuskIdentityGeneration
{
    /// <summary>Explicit values are accepted; otherwise the identity sequence supplies a value.</summary>
    ByDefault,

    /// <summary>Explicit values require PostgreSQL's <c>OVERRIDING SYSTEM VALUE</c> clause.</summary>
    Always,
}

/// <summary>PostgreSQL-specific property configuration extensions.</summary>
public static class BlueTuskPropertyBuilderExtensions
{
    /// <summary>Configures a PostgreSQL identity column with the selected generation behavior.</summary>
    public static PropertyBuilder UseBlueTuskIdentityColumn(
        this PropertyBuilder propertyBuilder,
        BlueTuskIdentityGeneration generation = BlueTuskIdentityGeneration.ByDefault)
    {
        ArgumentNullException.ThrowIfNull(propertyBuilder);
        if (!Enum.IsDefined(generation))
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        propertyBuilder.ValueGeneratedOnAdd();
        propertyBuilder.IsRequired();
        propertyBuilder.Metadata.SetAnnotation(
            BlueTuskValueGenerationAnnotations.IdentityGeneration,
            (int)generation);
        return propertyBuilder;
    }

    /// <inheritdoc cref="UseBlueTuskIdentityColumn(PropertyBuilder,BlueTuskIdentityGeneration)" />
    public static PropertyBuilder<TProperty> UseBlueTuskIdentityColumn<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder,
        BlueTuskIdentityGeneration generation = BlueTuskIdentityGeneration.ByDefault)
    {
        UseBlueTuskIdentityColumn((PropertyBuilder)propertyBuilder, generation);
        return propertyBuilder;
    }
}
