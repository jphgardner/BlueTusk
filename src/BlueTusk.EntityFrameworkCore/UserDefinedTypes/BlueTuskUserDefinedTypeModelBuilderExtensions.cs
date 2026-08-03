using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL enum, domain, composite, range, and multirange model extensions.</summary>
public static class BlueTuskUserDefinedTypeModelBuilderExtensions
{
    /// <summary>Adds or replaces a PostgreSQL enum type with ordered labels.</summary>
    public static ModelBuilder HasEnum(
        this ModelBuilder modelBuilder,
        string name,
        IReadOnlyList<string> labels,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var definitions = BlueTuskUserDefinedTypeMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, definitions with
        {
            Enums = Replace(definitions.Enums, new BlueTuskEnumTypeDefinition(name, schema, labels)),
        });
    }

    /// <summary>Adds or replaces a PostgreSQL domain definition.</summary>
    public static ModelBuilder HasDomain(
        this ModelBuilder modelBuilder,
        string name,
        string baseStoreType,
        Action<BlueTuskDomainBuilder>? buildAction = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskDomainBuilder(name, schema, baseStoreType);
        buildAction?.Invoke(builder);
        var definitions = BlueTuskUserDefinedTypeMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, definitions with
        {
            Domains = Replace(definitions.Domains, builder.Build()),
        });
    }

    /// <summary>Adds or replaces a standalone PostgreSQL composite type.</summary>
    public static ModelBuilder HasComposite(
        this ModelBuilder modelBuilder,
        string name,
        Action<BlueTuskCompositeTypeBuilder> buildAction,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(buildAction);
        var builder = new BlueTuskCompositeTypeBuilder(name, schema);
        buildAction(builder);
        var definitions = BlueTuskUserDefinedTypeMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, definitions with
        {
            Composites = Replace(definitions.Composites, builder.Build()),
        });
    }

    /// <summary>Adds or replaces a PostgreSQL range and its paired multirange type.</summary>
    public static ModelBuilder HasRange(
        this ModelBuilder modelBuilder,
        string name,
        string subtypeName,
        Action<BlueTuskRangeTypeBuilder>? buildAction = null,
        string? schema = null,
        string? subtypeSchema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskRangeTypeBuilder(name, schema, subtypeName, subtypeSchema);
        buildAction?.Invoke(builder);
        var definitions = BlueTuskUserDefinedTypeMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, definitions with
        {
            Ranges = Replace(definitions.Ranges, builder.Build()),
        });
    }

    /// <summary>Removes a provider-owned PostgreSQL type from the model.</summary>
    public static ModelBuilder HasNoUserDefinedType(
        this ModelBuilder modelBuilder,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = BlueTuskUserDefinedTypeMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, definitions with
        {
            Enums = definitions.Enums.Where(item => !HasName(item.Name, item.Schema, name, schema)).ToArray(),
            Domains = definitions.Domains.Where(item => !HasName(item.Name, item.Schema, name, schema)).ToArray(),
            Composites = definitions.Composites.Where(item => !HasName(item.Name, item.Schema, name, schema)).ToArray(),
            Ranges = definitions.Ranges.Where(item =>
                    !HasName(item.Name, item.Schema, name, schema) &&
                    !HasName(item.MultirangeType.Name, item.MultirangeType.Schema, name, schema))
                .ToArray(),
        });
    }

    /// <summary>Reads all provider-owned PostgreSQL type definitions from an EF model.</summary>
    public static BlueTuskUserDefinedTypeDefinitionSet GetUserDefinedTypes(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskUserDefinedTypeMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasUserDefinedTypes(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskUserDefinedTypeMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(
        ModelBuilder modelBuilder,
        BlueTuskUserDefinedTypeDefinitionSet definitions)
    {
        var serialized = BlueTuskUserDefinedTypeMetadata.Serialize(definitions);
        if (definitions.Enums.Count == 0 &&
            definitions.Domains.Count == 0 &&
            definitions.Composites.Count == 0 &&
            definitions.Ranges.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskUserDefinedTypeMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(BlueTuskUserDefinedTypeMetadata.AnnotationName, serialized);
        }

        return modelBuilder;
    }

    private static T[] Replace<T>(IReadOnlyList<T> definitions, T replacement)
        where T : class
    {
        var (replacementName, replacementSchema) = GetName(replacement);
        return definitions
            .Where(definition =>
            {
                var (name, schema) = GetName(definition);
                return !HasName(name, schema, replacementName, replacementSchema);
            })
            .Append(replacement)
            .ToArray();
    }

    private static (string Name, string? Schema) GetName<T>(T definition)
        where T : class =>
        definition switch
        {
            BlueTuskEnumTypeDefinition item => (item.Name, item.Schema),
            BlueTuskDomainTypeDefinition item => (item.Name, item.Schema),
            BlueTuskCompositeTypeDefinition item => (item.Name, item.Schema),
            BlueTuskRangeTypeDefinition item => (item.Name, item.Schema),
            _ => throw new InvalidOperationException($"Unknown PostgreSQL type definition '{typeof(T).Name}'."),
        };

    private static bool HasName(
        string leftName,
        string? leftSchema,
        string rightName,
        string? rightSchema) =>
        string.Equals(leftName, rightName, StringComparison.Ordinal) &&
        string.Equals(leftSchema, rightSchema, StringComparison.Ordinal);
}
