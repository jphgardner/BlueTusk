namespace BlueTusk.EntityFrameworkCore.EventTriggers;

/// <summary>Builds a provider-owned PostgreSQL event trigger.</summary>
public sealed class BlueTuskEventTriggerBuilder
{
    private readonly List<string> _tags = [];

    internal BlueTuskEventTriggerBuilder(
        string name,
        BlueTuskEventTriggerEvent @event,
        string functionName,
        string? functionSchema)
    {
        Name = name;
        Event = @event;
        Function = new BlueTuskEventTriggerFunction(functionName, functionSchema);
    }

    private string Name { get; }

    private BlueTuskEventTriggerEvent Event { get; }

    private BlueTuskEventTriggerFunction Function { get; }

    private BlueTuskEventTriggerEnabledMode EnabledMode { get; set; }

    /// <summary>Restricts firing to the supplied PostgreSQL command tags.</summary>
    public BlueTuskEventTriggerBuilder HasTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.AddRange(tags);
        return this;
    }

    /// <summary>Sets the event trigger's session-replication firing mode.</summary>
    public BlueTuskEventTriggerBuilder HasEnabledMode(BlueTuskEventTriggerEnabledMode enabledMode)
    {
        EnabledMode = enabledMode;
        return this;
    }

    /// <summary>Marks whether the event trigger is disabled.</summary>
    public BlueTuskEventTriggerBuilder IsDisabled(bool disabled = true)
    {
        EnabledMode = disabled ? BlueTuskEventTriggerEnabledMode.Disabled : BlueTuskEventTriggerEnabledMode.Origin;
        return this;
    }

    internal BlueTuskEventTriggerDefinition Build() => new(Name, Event, Function, _tags.ToArray(), EnabledMode);
}
