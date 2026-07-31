using BlueTusk.Extensions;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Collects immutable provider configuration before a data source is created.</summary>
public sealed class BlueTuskDataSourceBuilder : IBlueTuskPluginContext
{
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
        return this;
    }

    public BlueTuskDataSourceBuilder MapEnum<TEnum>(
        string postgresTypeName,
        IReadOnlyDictionary<TEnum, string>? labels = null)
        where TEnum : struct, Enum
    {
        var typeName = BlueTuskTypeName.Parse(postgresTypeName);
        Types.Register(typeName.Schema, typeName.Name, new BlueTuskEnumCodec<TEnum>(labels));
        return this;
    }

    public BlueTuskDataSourceBuilder MapComposite<T>(string postgresTypeName)
    {
        var typeName = BlueTuskTypeName.Parse(postgresTypeName);
        Types.Register(typeName.Schema, typeName.Name, new BlueTuskCompositeCodec<T>());
        return this;
    }

    public BlueTuskDataSource Build() => new(ConnectionString, Types.Build(), Features.Build());
}
