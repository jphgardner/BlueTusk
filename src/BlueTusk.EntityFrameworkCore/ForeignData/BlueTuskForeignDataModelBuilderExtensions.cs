using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.ForeignData;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures PostgreSQL foreign-data objects in an EF model.</summary>
public static class BlueTuskForeignDataModelBuilderExtensions
{
    public static ModelBuilder HasForeignDataWrapper(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskForeignDataWrapperBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BlueTuskForeignDataWrapperBuilder(name);
        configure(builder);
        var definition = builder.Build();
        BlueTuskForeignDataMetadata.Validate(definition);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            Wrappers = current.Wrappers.Where(item => item.Name != name).Append(definition).ToArray(),
        });
    }

    public static ModelBuilder HasForeignServer(
        this ModelBuilder modelBuilder,
        string name,
        string foreignDataWrapper,
        Action<BlueTuskForeignServerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignDataWrapper);
        var builder = new BlueTuskForeignServerBuilder(name, foreignDataWrapper);
        configure?.Invoke(builder);
        var definition = builder.Build();
        BlueTuskForeignDataMetadata.Validate(definition);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            Servers = current.Servers.Where(item => item.Name != name).Append(definition).ToArray(),
        });
    }

    public static ModelBuilder HasUserMapping(
        this ModelBuilder modelBuilder,
        string serverName,
        string userName,
        Action<BlueTuskUserMappingBuilder>? configure = null) =>
        AddOrReplaceUserMapping(modelBuilder, serverName, userName, configure);

    public static ModelBuilder HasPublicUserMapping(
        this ModelBuilder modelBuilder,
        string serverName,
        Action<BlueTuskUserMappingBuilder>? configure = null) =>
        AddOrReplaceUserMapping(modelBuilder, serverName, null, configure);

    public static ModelBuilder HasNoForeignDataWrapper(this ModelBuilder modelBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            Wrappers = current.Wrappers.Where(item => item.Name != name).ToArray(),
        });
    }

    public static ModelBuilder HasNoForeignServer(this ModelBuilder modelBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            Servers = current.Servers.Where(item => item.Name != name).ToArray(),
        });
    }

    public static ModelBuilder HasNoUserMapping(
        this ModelBuilder modelBuilder,
        string serverName,
        string? userName)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            UserMappings = current.UserMappings.Where(item =>
                    item.ServerName != serverName || item.UserName != userName)
                .ToArray(),
        });
    }

    public static BlueTuskForeignDataDefinitionSet GetForeignData(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskForeignDataMetadata.Get(model);
    }

    public static EntityTypeBuilder HasForeignTable(
        this EntityTypeBuilder entityTypeBuilder,
        string serverName,
        Action<BlueTuskForeignTableBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var builder = new BlueTuskForeignTableBuilder(serverName);
        configure?.Invoke(builder);
        var definition = builder.Build();
        BlueTuskForeignDataMetadata.Validate(definition);
        entityTypeBuilder.Metadata.SetAnnotation(
            BlueTuskForeignDataMetadata.ForeignTableAnnotationName,
            BlueTuskForeignDataMetadata.Serialize(definition));
        return entityTypeBuilder;
    }

    public static EntityTypeBuilder<TEntity> HasForeignTable<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string serverName,
        Action<BlueTuskForeignTableBuilder>? configure = null)
        where TEntity : class
    {
        HasForeignTable((EntityTypeBuilder)entityTypeBuilder, serverName, configure);
        return entityTypeBuilder;
    }

    public static EntityTypeBuilder HasNoForeignTable(this EntityTypeBuilder entityTypeBuilder)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        entityTypeBuilder.Metadata.RemoveAnnotation(BlueTuskForeignDataMetadata.ForeignTableAnnotationName);
        return entityTypeBuilder;
    }

    public static BlueTuskForeignTableDefinition? GetForeignTable(this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return BlueTuskForeignDataMetadata.GetForeignTable(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasForeignData(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskForeignDataMetadata.Deserialize(serializedDefinitions));
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasForeignTableDefinition(
        this EntityTypeBuilder entityTypeBuilder,
        string serializedDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        var definition = BlueTuskForeignDataMetadata.DeserializeForeignTable(serializedDefinition);
        entityTypeBuilder.Metadata.SetAnnotation(
            BlueTuskForeignDataMetadata.ForeignTableAnnotationName,
            BlueTuskForeignDataMetadata.Serialize(definition));
        return entityTypeBuilder;
    }

    private static ModelBuilder AddOrReplaceUserMapping(
        ModelBuilder modelBuilder,
        string serverName,
        string? userName,
        Action<BlueTuskUserMappingBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        if (userName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        }

        var builder = new BlueTuskUserMappingBuilder(serverName, userName);
        configure?.Invoke(builder);
        var definition = builder.Build();
        BlueTuskForeignDataMetadata.ValidateForModel(definition);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            UserMappings = current.UserMappings.Where(item =>
                    item.ServerName != serverName || item.UserName != userName)
                .Append(definition)
                .ToArray(),
        });
    }

    private static ModelBuilder Set(ModelBuilder modelBuilder, BlueTuskForeignDataDefinitionSet definitions)
    {
        if (definitions.Wrappers.Count == 0 && definitions.Servers.Count == 0 &&
            definitions.UserMappings.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskForeignDataMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskForeignDataMetadata.AnnotationName,
                BlueTuskForeignDataMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}
