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
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNull(configure);
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
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNullOrWhiteSpace(foreignDataWrapper);
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
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        var current = BlueTuskForeignDataMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, current with
        {
            Wrappers = current.Wrappers.Where(item => item.Name != name).ToArray(),
        });
    }

    public static ModelBuilder HasNoForeignServer(this ModelBuilder modelBuilder, string name)
    {
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
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
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(serverName);
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
        BlueTuskGuard.NotNull(model);
        return BlueTuskForeignDataMetadata.Get(model);
    }

    public static EntityTypeBuilder HasForeignTable(
        this EntityTypeBuilder entityTypeBuilder,
        string serverName,
        Action<BlueTuskForeignTableBuilder>? configure = null)
    {
        BlueTuskGuard.NotNull(entityTypeBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(serverName);
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
        BlueTuskGuard.NotNull(entityTypeBuilder);
        entityTypeBuilder.Metadata.RemoveAnnotation(BlueTuskForeignDataMetadata.ForeignTableAnnotationName);
        return entityTypeBuilder;
    }

    public static BlueTuskForeignTableDefinition? GetForeignTable(this IReadOnlyEntityType entityType)
    {
        BlueTuskGuard.NotNull(entityType);
        return BlueTuskForeignDataMetadata.GetForeignTable(entityType);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasForeignData(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        BlueTuskGuard.NotNull(modelBuilder);
        return Set(modelBuilder, BlueTuskForeignDataMetadata.Deserialize(serializedDefinitions));
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static EntityTypeBuilder HasForeignTableDefinition(
        this EntityTypeBuilder entityTypeBuilder,
        string serializedDefinition)
    {
        BlueTuskGuard.NotNull(entityTypeBuilder);
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
        BlueTuskGuard.NotNull(modelBuilder);
        BlueTuskGuard.NotNullOrWhiteSpace(serverName);
        if (userName is not null)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(userName);
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
