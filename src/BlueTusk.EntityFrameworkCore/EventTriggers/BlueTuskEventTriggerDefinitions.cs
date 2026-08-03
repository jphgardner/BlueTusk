namespace BlueTusk.EntityFrameworkCore.EventTriggers;

/// <summary>A PostgreSQL database-level event that can fire an event trigger.</summary>
public enum BlueTuskEventTriggerEvent
{
    /// <summary>Fires before a supported DDL command starts.</summary>
    DdlCommandStart,

    /// <summary>Fires after a supported DDL command completes.</summary>
    DdlCommandEnd,

    /// <summary>Fires after objects have been dropped.</summary>
    SqlDrop,

    /// <summary>Fires before a table rewrite.</summary>
    TableRewrite,

    /// <summary>Fires when an authenticated user logs in. PostgreSQL 17 or later.</summary>
    Login,
}

/// <summary>The session-replication modes in which an event trigger fires.</summary>
public enum BlueTuskEventTriggerEnabledMode
{
    /// <summary>Fires for origin and local sessions.</summary>
    Origin,

    /// <summary>Does not fire.</summary>
    Disabled,

    /// <summary>Fires only for replica sessions.</summary>
    Replica,

    /// <summary>Fires for every session-replication mode.</summary>
    Always,
}

/// <summary>A schema-qualified event-trigger function.</summary>
public sealed record BlueTuskEventTriggerFunction(string Name, string? Schema = null);

/// <summary>A provider-owned PostgreSQL event trigger.</summary>
public sealed record BlueTuskEventTriggerDefinition(
    string Name,
    BlueTuskEventTriggerEvent Event,
    BlueTuskEventTriggerFunction Function,
    IReadOnlyList<string> Tags,
    BlueTuskEventTriggerEnabledMode EnabledMode);

/// <summary>All provider-owned PostgreSQL event triggers in a model.</summary>
public sealed record BlueTuskEventTriggerDefinitionSet(IReadOnlyList<BlueTuskEventTriggerDefinition> EventTriggers)
{
    /// <summary>An empty event-trigger definition set.</summary>
    public static BlueTuskEventTriggerDefinitionSet Empty { get; } = new([]);
}
