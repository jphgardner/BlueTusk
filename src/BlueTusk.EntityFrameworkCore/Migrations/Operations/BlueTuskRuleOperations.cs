using BlueTusk.EntityFrameworkCore.Rules;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateRuleOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required BlueTuskRuleDefinition Definition { get; init; }
    public bool OrReplace { get; init; }
}

public sealed class DropRuleOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
}

public sealed class RenameRuleOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class AlterRuleEnabledModeOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public required BlueTuskRuleEnabledMode EnabledMode { get; init; }
}
