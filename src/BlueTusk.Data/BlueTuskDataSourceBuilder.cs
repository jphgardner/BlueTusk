using BlueTusk.Extensions;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Collects immutable provider configuration before a data source is created.</summary>
/// <remarks>The executable data source will be introduced with the first end-to-end connection milestone.</remarks>
public sealed class BlueTuskDataSourceBuilder : IBlueTuskPluginContext
{
    private readonly List<IBlueTuskPlugin> _plugins = [];

    public BlueTuskDataSourceBuilder(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _ = new BlueTuskConnectionStringBuilder(connectionString);
    }

    internal string ConnectionString { get; }

    public BlueTuskTypeRegistryBuilder Types { get; } = new();

    public BlueTuskFeatureRegistryBuilder Features { get; } = new();

    public BlueTuskDataSourceBuilder UsePlugin(IBlueTuskPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        plugin.Configure(this);
        _plugins.Add(plugin);
        return this;
    }

    internal IReadOnlyList<IBlueTuskPlugin> Plugins => _plugins;

    public BlueTuskDataSource Build() => new(ConnectionString);
}
