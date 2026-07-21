namespace BlueTusk.Extensions;

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

    public IReadOnlyDictionary<string, object> Build() =>
        new Dictionary<string, object>(_features, StringComparer.Ordinal);
}

