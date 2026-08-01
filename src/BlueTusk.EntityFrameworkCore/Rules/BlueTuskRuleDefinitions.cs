namespace BlueTusk.EntityFrameworkCore.Rules;

public enum BlueTuskRuleEvent
{
    Select,
    Insert,
    Update,
    Delete,
}

public enum BlueTuskRuleEnabledMode
{
    Origin,
    Disabled,
    Replica,
    Always,
}

public sealed record BlueTuskRuleDefinition(
    string Name,
    BlueTuskRuleEvent Event,
    bool IsInstead,
    string? ConditionSql,
    string? ActionSql,
    BlueTuskRuleEnabledMode EnabledMode,
    string? CanonicalCreateSql = null);

public sealed record BlueTuskRuleTableDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<BlueTuskRuleDefinition> Rules);
