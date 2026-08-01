namespace BlueTusk.EntityFrameworkCore.Triggers;

public enum BlueTuskTriggerTiming
{
    Before,
    After,
    InsteadOf,
}

public enum BlueTuskTriggerEventKind
{
    Insert,
    Update,
    Delete,
    Truncate,
}

public enum BlueTuskTriggerOrientation
{
    Statement,
    Row,
}

public enum BlueTuskTriggerEnabledMode
{
    Origin,
    Disabled,
    Replica,
    Always,
}

public sealed record BlueTuskTriggerEventDefinition(
    BlueTuskTriggerEventKind Kind,
    IReadOnlyList<string> UpdateColumns);

public sealed record BlueTuskTriggerDefinition(
    string Name,
    BlueTuskTriggerTiming Timing,
    IReadOnlyList<BlueTuskTriggerEventDefinition> Events,
    BlueTuskTriggerOrientation Orientation,
    string? FunctionName,
    string? FunctionSchema,
    IReadOnlyList<string> Arguments,
    string? WhenSql,
    string? OldTransitionTable,
    string? NewTransitionTable,
    bool IsConstraint,
    string? ReferencedTable,
    string? ReferencedTableSchema,
    bool IsDeferrable,
    bool IsInitiallyDeferred,
    BlueTuskTriggerEnabledMode EnabledMode,
    string? CanonicalCreateSql = null,
    string? ExtensionDependency = null);

public sealed record BlueTuskTriggerTableDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<BlueTuskTriggerDefinition> Triggers);
