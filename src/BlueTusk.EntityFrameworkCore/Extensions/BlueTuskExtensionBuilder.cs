namespace BlueTusk.EntityFrameworkCore.Extensions;

/// <summary>Builds a provider-owned PostgreSQL extension installation.</summary>
public sealed class BlueTuskExtensionBuilder
{
    private readonly List<string> _dependencies = [];

    internal BlueTuskExtensionBuilder(string name)
    {
        Name = name;
    }

    private string Name { get; }

    private string? Schema { get; set; }

    private string? Version { get; set; }

    private bool InstallDependenciesValue { get; set; }

    /// <summary>Sets the schema in which the extension should install its objects.</summary>
    public BlueTuskExtensionBuilder UseSchema(string schema)
    {
        Schema = schema;
        return this;
    }

    /// <summary>Pins the extension version installed or selected by migrations.</summary>
    public BlueTuskExtensionBuilder HasVersion(string version)
    {
        Version = version;
        return this;
    }

    /// <summary>Declares another extension that must be installed first.</summary>
    public BlueTuskExtensionBuilder DependsOnExtension(string name)
    {
        _dependencies.Add(name);
        return this;
    }

    /// <summary>Uses <c>CASCADE</c> during installation to install missing extension dependencies.</summary>
    public BlueTuskExtensionBuilder InstallDependencies(bool installDependencies = true)
    {
        InstallDependenciesValue = installDependencies;
        return this;
    }

    internal BlueTuskExtensionDefinition Build() => new(
        Name,
        Schema,
        Version,
        _dependencies.ToArray(),
        InstallDependenciesValue);
}
