using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.TableInheritance;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL table-inheritance extensions for EF models.</summary>
public static class BlueTuskTableInheritanceBuilderExtensions
{
    /// <summary>Adds a named direct parent to a PostgreSQL table-inheritance hierarchy.</summary>
    public static EntityTypeBuilder InheritsFromTable(
        this EntityTypeBuilder entityBuilder,
        string parentTable,
        string? parentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTable);
        if (parentSchema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentSchema);
        }

        var existing = BlueTuskTableInheritanceMetadata.Get(entityBuilder.Metadata);
        var parents = existing?.Parents.ToList() ?? [];
        parents.Add(new BlueTuskInheritedTableDefinition(parentTable, parentSchema));
        Set(entityBuilder, new BlueTuskTableInheritanceDefinition(parents));
        return entityBuilder;
    }

    /// <summary>Adds the table mapped by <typeparamref name="TParent"/> as a direct inheritance parent.</summary>
    public static EntityTypeBuilder InheritsFromTable<TParent>(
        this EntityTypeBuilder entityBuilder)
        where TParent : class
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        var parent = entityBuilder.Metadata.Model.FindEntityType(typeof(TParent))
            ?? throw new InvalidOperationException(
                $"Entity type '{typeof(TParent).Name}' must be added to the model before it can be an inheritance parent.");
        var parentTable = parent.GetTableName()
            ?? throw new InvalidOperationException(
                $"Entity type '{parent.DisplayName()}' is not mapped to a table.");
        InheritsFromTable(entityBuilder, parentTable, parent.GetSchema());
        return entityBuilder;
    }

    /// <summary>Replaces all direct parents for a PostgreSQL table-inheritance hierarchy.</summary>
    public static EntityTypeBuilder HasTableInheritance(
        this EntityTypeBuilder entityBuilder,
        params BlueTuskInheritedTableDefinition[] parents)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        ArgumentNullException.ThrowIfNull(parents);
        Set(entityBuilder, new BlueTuskTableInheritanceDefinition(parents));
        return entityBuilder;
    }

    /// <summary>Removes PostgreSQL table-inheritance metadata from an entity table.</summary>
    public static EntityTypeBuilder HasNoTableInheritance(this EntityTypeBuilder entityBuilder)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        entityBuilder.Metadata.RemoveAnnotation(BlueTuskTableInheritanceMetadata.AnnotationName);
        return entityBuilder;
    }

    /// <summary>Reads PostgreSQL table-inheritance metadata from an EF entity type.</summary>
    public static BlueTuskTableInheritanceDefinition? GetTableInheritance(
        this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return BlueTuskTableInheritanceMetadata.Get(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasTableInheritance(
        this EntityTypeBuilder entityBuilder,
        string serializedDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityBuilder);
        Set(entityBuilder, BlueTuskTableInheritanceMetadata.Deserialize(serializedDefinition));
        return entityBuilder;
    }

    private static void Set(
        EntityTypeBuilder entityBuilder,
        BlueTuskTableInheritanceDefinition definition)
    {
        BlueTuskTableInheritanceMetadata.Validate(definition);
        entityBuilder.Metadata.SetAnnotation(
            BlueTuskTableInheritanceMetadata.AnnotationName,
            BlueTuskTableInheritanceMetadata.Serialize(definition));
    }
}
