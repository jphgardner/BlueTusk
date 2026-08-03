using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Partitioning;

/// <summary>Builds an immutable PostgreSQL declarative-partition tree in EF model metadata.</summary>
public sealed class BlueTuskPartitioningBuilder
{
    private readonly IMutableEntityType _entityType;
    private readonly IReadOnlyList<PartitionPath> _path;

    internal BlueTuskPartitioningBuilder(
        IMutableEntityType entityType,
        IReadOnlyList<(string Name, string? Schema)> path)
    {
        _entityType = entityType;
        _path = path.Select(item => new PartitionPath(item.Name, item.Schema)).ToArray();
    }

    /// <summary>Adds or replaces a child partition.</summary>
    public BlueTuskPartitioningBuilder HasPartition(
        string name,
        BlueTuskPartitionBound bound,
        string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bound);
        UpdateCurrent(
            definition =>
            {
                ValidateBound(definition, bound);
                var partitions = definition.Partitions
                    .Where(partition =>
                        !string.Equals(partition.Name, name, StringComparison.Ordinal) ||
                        !string.Equals(partition.Schema, schema, StringComparison.Ordinal))
                    .Append(new BlueTuskPartitionDefinition(name, schema, bound))
                    .OrderBy(partition => partition.Schema, StringComparer.Ordinal)
                    .ThenBy(partition => partition.Name, StringComparer.Ordinal)
                    .ToArray();
                ValidateDefaultCount(partitions);
                return definition with { Partitions = partitions };
            });
        return this;
    }

    /// <summary>Adds or replaces a single-key range partition.</summary>
    public BlueTuskPartitioningBuilder HasRangePartition(
        string name,
        BlueTuskPartitionValue from,
        BlueTuskPartitionValue to,
        string? schema = null) =>
        HasPartition(name, BlueTuskPartitionBound.Range(from, to), schema);

    /// <summary>Adds or replaces a single-key list partition.</summary>
    public BlueTuskPartitioningBuilder HasListPartition(
        string name,
        IReadOnlyList<BlueTuskPartitionValue> values,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return HasPartition(name, BlueTuskPartitionBound.List(values.ToArray()), schema);
    }

    /// <summary>Adds or replaces a hash partition.</summary>
    public BlueTuskPartitioningBuilder HasHashPartition(
        string name,
        int modulus,
        int remainder,
        string? schema = null) =>
        HasPartition(name, BlueTuskPartitionBound.Hash(modulus, remainder), schema);

    /// <summary>Adds or replaces the default partition.</summary>
    public BlueTuskPartitioningBuilder HasDefaultPartition(string name, string? schema = null) =>
        HasPartition(name, BlueTuskPartitionBound.Default(), schema);

    /// <summary>Removes a configured child partition.</summary>
    public BlueTuskPartitioningBuilder HasNoPartition(string name, string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        UpdateCurrent(
            definition => definition with
            {
                Partitions = definition.Partitions
                    .Where(partition =>
                        !string.Equals(partition.Name, name, StringComparison.Ordinal) ||
                        !string.Equals(partition.Schema, schema, StringComparison.Ordinal))
                    .ToArray(),
            });
        return this;
    }

    /// <summary>Configures an existing child partition as a subpartitioned table.</summary>
    public BlueTuskPartitioningBuilder HasSubpartitioning(
        string partitionName,
        BlueTuskPartitionStrategy strategy,
        IReadOnlyList<BlueTuskPartitionKeyDefinition> keys,
        Action<BlueTuskPartitioningBuilder> configure,
        string? partitionSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(configure);
        ValidateKeys(keys, keySql: null);
        var subpartitioning = new BlueTuskPartitioningDefinition(strategy, keys.ToArray(), []);
        UpdateCurrent(
            definition => definition with
            {
                Partitions = definition.Partitions.Select(
                        partition =>
                            string.Equals(partition.Name, partitionName, StringComparison.Ordinal) &&
                            string.Equals(partition.Schema, partitionSchema, StringComparison.Ordinal)
                                ? partition with { Partitioning = subpartitioning }
                                : partition)
                    .ToArray(),
            },
            requirePartition: new PartitionPath(partitionName, partitionSchema));

        var path = _path
            .Select(item => (item.Name, item.Schema))
            .Append((partitionName, partitionSchema))
            .ToArray();
        configure(new BlueTuskPartitioningBuilder(_entityType, path));
        return this;
    }

    private void UpdateCurrent(
        Func<BlueTuskPartitioningDefinition, BlueTuskPartitioningDefinition> update,
        PartitionPath? requirePartition = null)
    {
        var root = BlueTuskPartitionMetadata.Get(_entityType)
            ?? throw new InvalidOperationException("The entity does not have BlueTusk partitioning metadata.");
        if (requirePartition is { } required &&
            !GetCurrent(root, _path).Partitions.Any(
                partition =>
                    partition.Name == required.Name && partition.Schema == required.Schema))
        {
            throw new InvalidOperationException(
                $"Partition '{required.Schema}.{required.Name}' must be configured before subpartitioning it.");
        }

        var updated = Update(root, _path, 0, update);
        _entityType.SetAnnotation(
            BlueTuskPartitionMetadata.AnnotationName,
            BlueTuskPartitionMetadata.Serialize(updated));
    }

    private static BlueTuskPartitioningDefinition Update(
        BlueTuskPartitioningDefinition definition,
        IReadOnlyList<PartitionPath> path,
        int index,
        Func<BlueTuskPartitioningDefinition, BlueTuskPartitioningDefinition> update)
    {
        if (index == path.Count)
        {
            return update(definition);
        }

        var item = path[index];
        var found = false;
        var partitions = definition.Partitions.Select(
                partition =>
                {
                    if (partition.Name != item.Name || partition.Schema != item.Schema)
                    {
                        return partition;
                    }

                    found = true;
                    var child = partition.Partitioning
                        ?? throw new InvalidOperationException(
                            $"Partition '{item.Schema}.{item.Name}' is not subpartitioned.");
                    return partition with { Partitioning = Update(child, path, index + 1, update) };
                })
            .ToArray();
        return found
            ? definition with { Partitions = partitions }
            : throw new InvalidOperationException($"Partition '{item.Schema}.{item.Name}' was not found.");
    }

    private static BlueTuskPartitioningDefinition GetCurrent(
        BlueTuskPartitioningDefinition definition,
        IReadOnlyList<PartitionPath> path)
    {
        foreach (var item in path)
        {
            definition = definition.Partitions.Single(
                    partition => partition.Name == item.Name && partition.Schema == item.Schema)
                .Partitioning!;
        }

        return definition;
    }

    internal static void ValidateDefinition(BlueTuskPartitioningDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Keys);
        ArgumentNullException.ThrowIfNull(definition.Partitions);
        if (!Enum.IsDefined(definition.Strategy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                definition.Strategy,
                "Unknown partition strategy.");
        }

        ValidateKeys(definition.Keys, definition.KeySql);
        if (definition.Strategy == BlueTuskPartitionStrategy.List &&
            definition.Keys.Count > 1)
        {
            throw new ArgumentException(
                "PostgreSQL LIST partitioning requires exactly one key.",
                nameof(definition));
        }

        ValidateDefaultCount(definition.Partitions);
        var duplicate = definition.Partitions
            .GroupBy(partition => (partition.Schema, partition.Name))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Partition '{duplicate.Key.Schema}.{duplicate.Key.Name}' is configured more than once.",
                nameof(definition));
        }

        foreach (var partition in definition.Partitions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition.Name);
            ArgumentNullException.ThrowIfNull(partition.Bound);
            ValidateBound(definition, partition.Bound);
            if (partition.Partitioning is { } subpartitioning)
            {
                ValidateDefinition(subpartitioning);
            }
        }
    }

    private static void ValidateKeys(
        IReadOnlyList<BlueTuskPartitionKeyDefinition> keys,
        string? keySql)
    {
        if (keys.Count == 0 && string.IsNullOrWhiteSpace(keySql))
        {
            throw new ArgumentException("Partitioning requires at least one key.", nameof(keys));
        }

        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(key.Expression);
            ValidateQualifiedIdentifier(key.Collation, nameof(key.Collation));
            ValidateQualifiedIdentifier(key.OperatorClass, nameof(key.OperatorClass));
        }
    }

    private static void ValidateQualifiedIdentifier(string? value, string parameterName)
    {
        if (value is not null && value.Split('.').Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Qualified PostgreSQL identifiers cannot contain empty components.",
                parameterName);
        }
    }

    private static void ValidateBound(
        BlueTuskPartitioningDefinition definition,
        BlueTuskPartitionBound bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        if (!Enum.IsDefined(bound.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(bound), bound.Kind, "Unknown partition bound kind.");
        }

        if (bound.Kind == BlueTuskPartitionBoundKind.Sql)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bound.Sql);
            return;
        }

        if (bound.Kind == BlueTuskPartitionBoundKind.Default)
        {
            return;
        }

        var keyCount = definition.Keys.Count;
        switch (definition.Strategy, bound.Kind)
        {
            case (BlueTuskPartitionStrategy.Range, BlueTuskPartitionBoundKind.Range):
                if (bound.From is null || bound.To is null ||
                    bound.From.Length == 0 || bound.From.Length != bound.To.Length ||
                    bound.From.Any(string.IsNullOrWhiteSpace) || bound.To.Any(string.IsNullOrWhiteSpace) ||
                    (keyCount > 0 && bound.From.Length != keyCount))
                {
                    throw new ArgumentException("Range bounds require matching lower/upper values for every partition key.", nameof(bound));
                }

                break;
            case (BlueTuskPartitionStrategy.List, BlueTuskPartitionBoundKind.List):
                if (bound.Values is null || bound.Values.Length == 0 || bound.Values.Any(tuple =>
                        tuple is null || tuple.Length != 1 || tuple.Any(string.IsNullOrWhiteSpace)))
                {
                    throw new ArgumentException("List bounds require one or more single-key values.", nameof(bound));
                }

                break;
            case (BlueTuskPartitionStrategy.Hash, BlueTuskPartitionBoundKind.Hash):
                _ = BlueTuskPartitionBound.Hash(bound.Modulus, bound.Remainder);
                break;
            default:
                throw new ArgumentException(
                    $"A {definition.Strategy} partitioned table cannot use a {bound.Kind} bound.",
                    nameof(bound));
        }
    }

    private static void ValidateDefaultCount(IReadOnlyList<BlueTuskPartitionDefinition> partitions)
    {
        if (partitions.Count(partition => partition.Bound.Kind == BlueTuskPartitionBoundKind.Default) > 1)
        {
            throw new ArgumentException("A partitioned table can have only one default partition.", nameof(partitions));
        }
    }

    private readonly record struct PartitionPath(string Name, string? Schema);
}
