using System.ComponentModel;
using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Model-builder extensions for provider-owned PostgreSQL functions and procedures.</summary>
public static class BlueTuskRoutineModelBuilderExtensions
{
    /// <summary>Adds or replaces a model-authored PostgreSQL function.</summary>
    public static ModelBuilder HasBlueTuskFunction(
        this ModelBuilder modelBuilder,
        string name,
        string returnStoreType,
        string bodySql,
        Action<BlueTuskFunctionBuilder>? buildAction = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskFunctionBuilder(name, schema, returnStoreType, bodySql);
        buildAction?.Invoke(builder);
        return HasBlueTuskRoutine(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a model-authored PostgreSQL procedure.</summary>
    public static ModelBuilder HasBlueTuskProcedure(
        this ModelBuilder modelBuilder,
        string name,
        string bodySql,
        Action<BlueTuskProcedureBuilder>? buildAction = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var builder = new BlueTuskProcedureBuilder(name, schema, bodySql);
        buildAction?.Invoke(builder);
        return HasBlueTuskRoutine(modelBuilder, builder.Build());
    }

    /// <summary>Adds or replaces a canonical provider-owned PostgreSQL routine definition.</summary>
    public static ModelBuilder HasBlueTuskRoutine(
        this ModelBuilder modelBuilder,
        BlueTuskRoutineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        BlueTuskRoutineMetadata.Validate(definition);
        definition = BlueTuskRoutineMetadata.Normalize(definition);
        var definitions = BlueTuskRoutineMetadata.Get(modelBuilder.Model);
        var key = BlueTuskRoutineMetadata.RoutineKey.Create(definition);
        return Set(modelBuilder, new BlueTuskRoutineDefinitionSet(
            definitions.Routines
                .Where(item => BlueTuskRoutineMetadata.RoutineKey.Create(item) != key)
                .Append(definition)
                .ToArray()));
    }

    /// <summary>Removes one PostgreSQL routine overload from the model.</summary>
    public static ModelBuilder HasNoBlueTuskRoutine(
        this ModelBuilder modelBuilder,
        BlueTuskRoutineKind kind,
        string name,
        string inputArgumentTypesSql = "",
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inputArgumentTypesSql);
        var key = new BlueTuskRoutineMetadata.RoutineKey(kind, schema, name, inputArgumentTypesSql.Trim());
        var definitions = BlueTuskRoutineMetadata.Get(modelBuilder.Model);
        return Set(modelBuilder, new BlueTuskRoutineDefinitionSet(
            definitions.Routines
                .Where(item => BlueTuskRoutineMetadata.RoutineKey.Create(item) != key)
                .ToArray()));
    }

    /// <summary>Reads all provider-owned PostgreSQL routine definitions from an EF model.</summary>
    public static BlueTuskRoutineDefinitionSet GetBlueTuskRoutines(this IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BlueTuskRoutineMetadata.Get(model);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ModelBuilder HasBlueTuskRoutines(
        this ModelBuilder modelBuilder,
        string serializedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return Set(modelBuilder, BlueTuskRoutineMetadata.Deserialize(serializedDefinitions));
    }

    private static ModelBuilder Set(
        ModelBuilder modelBuilder,
        BlueTuskRoutineDefinitionSet definitions)
    {
        if (definitions.Routines.Count == 0)
        {
            modelBuilder.Model.RemoveAnnotation(BlueTuskRoutineMetadata.AnnotationName);
        }
        else
        {
            modelBuilder.Model.SetAnnotation(
                BlueTuskRoutineMetadata.AnnotationName,
                BlueTuskRoutineMetadata.Serialize(definitions));
        }

        return modelBuilder;
    }
}
