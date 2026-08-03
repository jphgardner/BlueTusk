using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL row-level security extensions for EF models.</summary>
public static class BlueTuskRowLevelSecurityBuilderExtensions
{
    /// <summary>Configures row-level security for an entity table.</summary>
    public static BlueTuskRowLevelSecurityBuilder UseRowLevelSecurity(
        this EntityTypeBuilder entityBuilder,
        bool enabled = true,
        bool forced = false)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        var definition = new BlueTuskRowLevelSecurityDefinition(enabled, forced, []);
        entityBuilder.Metadata.SetAnnotation(
            BlueTuskRowLevelSecurityMetadata.AnnotationName,
            BlueTuskRowLevelSecurityMetadata.Serialize(definition));
        return new BlueTuskRowLevelSecurityBuilder(entityBuilder.Metadata);
    }

    /// <summary>Removes row-level security metadata from an entity table.</summary>
    public static EntityTypeBuilder HasNoRowLevelSecurity(this EntityTypeBuilder entityBuilder)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        entityBuilder.Metadata.RemoveAnnotation(BlueTuskRowLevelSecurityMetadata.AnnotationName);
        return entityBuilder;
    }

    /// <summary>Reads row-level security metadata from an EF entity type.</summary>
    public static BlueTuskRowLevelSecurityDefinition? GetRowLevelSecurity(
        this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return BlueTuskRowLevelSecurityMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasRowLevelSecurity(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        var definition = BlueTuskRowLevelSecurityMetadata.Deserialize(serializedDefinition);
        BlueTuskRowLevelSecurityBuilder.ValidateDefinition(definition);
        entityBuilder.Metadata.SetAnnotation(
            BlueTuskRowLevelSecurityMetadata.AnnotationName,
            BlueTuskRowLevelSecurityMetadata.Serialize(definition));
        return entityBuilder;
    }
}
