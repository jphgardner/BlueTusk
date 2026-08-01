using System.Collections.Frozen;

namespace BlueTusk.Extensions;

/// <summary>An immutable snapshot of optional provider features configured for one data source.</summary>
public sealed class BlueTuskFeatureRegistry
{
    private readonly FrozenDictionary<string, object> _features;

    internal BlueTuskFeatureRegistry(IReadOnlyDictionary<string, object> features)
    {
        _features = features.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static BlueTuskFeatureRegistry Empty { get; } =
        new(new Dictionary<string, object>(StringComparer.Ordinal));

    public int Count => _features.Count;

    public IEnumerable<string> Names => _features.Keys;

    public bool Contains(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _features.ContainsKey(name);
    }

    public bool TryGet(string name, out object? feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _features.TryGetValue(name, out feature);
    }

    public bool TryGet<TFeature>(string name, out TFeature feature)
        where TFeature : notnull
    {
        if (TryGet(name, out var registered) && registered is TFeature typed)
        {
            feature = typed;
            return true;
        }

        feature = default!;
        return false;
    }

    public TFeature GetRequired<TFeature>(string name)
        where TFeature : notnull =>
        TryGet<TFeature>(name, out var feature)
            ? feature
            : throw new KeyNotFoundException(
                $"Feature '{name}' is not registered as {typeof(TFeature).FullName}.");
}

/// <summary>Collects provider features contributed by built-in and extension packages.</summary>
public sealed class BlueTuskFeatureRegistryBuilder
{
    private readonly Dictionary<string, object> _features = new(StringComparer.Ordinal);

    public BlueTuskFeatureRegistryBuilder Register<TFeature>(string name, TFeature feature)
        where TFeature : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(feature);

        if (!_features.TryAdd(name, feature))
        {
            throw new InvalidOperationException($"Feature '{name}' is already registered.");
        }

        return this;
    }

    public BlueTuskFeatureRegistry Build() => new(_features);
}
