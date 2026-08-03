using System.Globalization;

namespace BlueTusk.EntityFrameworkCore.Tablespaces;

/// <summary>Builds a provider-owned PostgreSQL tablespace.</summary>
public sealed class BlueTuskTablespaceBuilder
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    internal BlueTuskTablespaceBuilder(string name, string location)
    {
        Name = name;
        Location = location;
    }

    private string Name { get; }

    private string Location { get; }

    private string? Owner { get; set; }

    private string? Comment { get; set; }

    /// <summary>Assigns the tablespace to the specified PostgreSQL role.</summary>
    public BlueTuskTablespaceBuilder OwnedBy(string owner)
    {
        Owner = owner;
        return this;
    }

    /// <summary>Sets a supported PostgreSQL tablespace option.</summary>
    public BlueTuskTablespaceBuilder HasOption(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _options[name] = value;
        return this;
    }

    /// <summary>Sets the sequential-page planner cost override.</summary>
    public BlueTuskTablespaceBuilder HasSequentialPageCost(double value) =>
        HasOption("seq_page_cost", value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Sets the random-page planner cost override.</summary>
    public BlueTuskTablespaceBuilder HasRandomPageCost(double value) =>
        HasOption("random_page_cost", value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Sets the effective I/O concurrency override.</summary>
    public BlueTuskTablespaceBuilder HasEffectiveIoConcurrency(int value) =>
        HasOption("effective_io_concurrency", value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Sets the maintenance I/O concurrency override.</summary>
    public BlueTuskTablespaceBuilder HasMaintenanceIoConcurrency(int value) =>
        HasOption("maintenance_io_concurrency", value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Sets the shared-object comment.</summary>
    public BlueTuskTablespaceBuilder HasComment(string? comment)
    {
        Comment = comment;
        return this;
    }

    internal BlueTuskTablespaceDefinition Build() => new(
        Name,
        Location,
        Owner,
        _options.Select(option => new BlueTuskTablespaceOptionDefinition(option.Key, option.Value)).ToArray(),
        Comment);
}
