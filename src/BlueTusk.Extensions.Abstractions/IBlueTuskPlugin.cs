using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions;

/// <summary>Configures an optional BlueTusk extension without coupling it to provider internals.</summary>
public interface IBlueTuskPlugin
{
    void Configure(IBlueTuskPluginContext context);
}

public interface IBlueTuskPluginContext
{
    BlueTuskTypeRegistryBuilder Types { get; }

    BlueTuskFeatureRegistryBuilder Features { get; }
}

public interface IBlueTuskTypePlugin
{
    void RegisterTypes(BlueTuskTypeRegistryBuilder types);
}

public interface IBlueTuskFeaturePlugin
{
    void RegisterFeatures(BlueTuskFeatureRegistryBuilder features);
}

