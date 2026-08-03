namespace BlueTusk.EntityFrameworkCore.ForeignData;

/// <summary>Builds a foreign-data wrapper definition.</summary>
public sealed class BlueTuskForeignDataWrapperBuilder
{
    private readonly List<BlueTuskForeignOptionDefinition> _options = [];

    internal BlueTuskForeignDataWrapperBuilder(string name)
    {
        Name = name;
    }

    private string Name { get; }
    private string? HandlerFunction { get; set; }
    private string? ValidatorFunction { get; set; }
    private string? ConnectionFunction { get; set; }

    /// <summary>Uses the specified schema-qualified handler function.</summary>
    public BlueTuskForeignDataWrapperBuilder HasHandler(string? function)
    {
        HandlerFunction = function;
        return this;
    }

    /// <summary>Uses the specified schema-qualified validator function.</summary>
    public BlueTuskForeignDataWrapperBuilder HasValidator(string? function)
    {
        ValidatorFunction = function;
        return this;
    }

    /// <summary>Uses the PostgreSQL 19 connection function for subscription sources.</summary>
    public BlueTuskForeignDataWrapperBuilder HasConnectionFunction(string? function)
    {
        ConnectionFunction = function;
        return this;
    }

    /// <summary>Adds or replaces a wrapper-specific option.</summary>
    public BlueTuskForeignDataWrapperBuilder HasOption(string name, string value)
    {
        SetOption(_options, name, value);
        return this;
    }

    internal BlueTuskForeignDataWrapperDefinition Build() => new(
        Name,
        HandlerFunction,
        ValidatorFunction,
        ConnectionFunction,
        _options.ToArray());

    internal static void SetOption(
        List<BlueTuskForeignOptionDefinition> options,
        string name,
        string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        options.RemoveAll(option => string.Equals(option.Name, name, StringComparison.Ordinal));
        options.Add(new BlueTuskForeignOptionDefinition(name, value));
    }
}

/// <summary>Builds a foreign-server definition.</summary>
public sealed class BlueTuskForeignServerBuilder
{
    private readonly List<BlueTuskForeignOptionDefinition> _options = [];

    internal BlueTuskForeignServerBuilder(string name, string foreignDataWrapper)
    {
        Name = name;
        ForeignDataWrapper = foreignDataWrapper;
    }

    private string Name { get; }
    private string ForeignDataWrapper { get; }
    private string? Type { get; set; }
    private string? Version { get; set; }

    /// <summary>Sets the wrapper-defined server type.</summary>
    public BlueTuskForeignServerBuilder HasType(string? type)
    {
        Type = type;
        return this;
    }

    /// <summary>Sets the wrapper-defined server version.</summary>
    public BlueTuskForeignServerBuilder HasVersion(string? version)
    {
        Version = version;
        return this;
    }

    /// <summary>Adds or replaces a server-specific option.</summary>
    public BlueTuskForeignServerBuilder HasOption(string name, string value)
    {
        BlueTuskForeignDataWrapperBuilder.SetOption(_options, name, value);
        return this;
    }

    internal BlueTuskForeignServerDefinition Build() => new(
        Name,
        ForeignDataWrapper,
        Type,
        Version,
        _options.ToArray());
}

/// <summary>Builds a foreign-server user mapping.</summary>
public sealed class BlueTuskUserMappingBuilder
{
    private readonly List<BlueTuskForeignOptionDefinition> _options = [];

    internal BlueTuskUserMappingBuilder(string serverName, string? userName)
    {
        ServerName = serverName;
        UserName = userName;
    }

    private string ServerName { get; }
    private string? UserName { get; }

    /// <summary>Adds or replaces a mapping-specific option.</summary>
    public BlueTuskUserMappingBuilder HasOption(string name, string value)
    {
        BlueTuskForeignDataWrapperBuilder.SetOption(_options, name, value);
        return this;
    }

    internal BlueTuskUserMappingDefinition Build() => new(
        ServerName,
        UserName,
        _options.ToArray());
}

/// <summary>Builds table and column options for a PostgreSQL foreign table.</summary>
public sealed class BlueTuskForeignTableBuilder
{
    private readonly List<BlueTuskForeignOptionDefinition> _options = [];
    private readonly Dictionary<string, List<BlueTuskForeignOptionDefinition>> _columns =
        new(StringComparer.Ordinal);

    internal BlueTuskForeignTableBuilder(string serverName)
    {
        ServerName = serverName;
    }

    private string ServerName { get; }

    /// <summary>Adds or replaces a table-specific option.</summary>
    public BlueTuskForeignTableBuilder HasOption(string name, string value)
    {
        BlueTuskForeignDataWrapperBuilder.SetOption(_options, name, value);
        return this;
    }

    /// <summary>Adds or replaces an option for the mapped store column name.</summary>
    public BlueTuskForeignTableBuilder HasColumnOption(string columnName, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!_columns.TryGetValue(columnName, out var options))
        {
            options = [];
            _columns.Add(columnName, options);
        }

        BlueTuskForeignDataWrapperBuilder.SetOption(options, name, value);
        return this;
    }

    internal BlueTuskForeignTableDefinition Build() => new(
        ServerName,
        _options.ToArray(),
        _columns.Select(column => new BlueTuskForeignColumnDefinition(column.Key, column.Value.ToArray()))
            .ToArray());
}
