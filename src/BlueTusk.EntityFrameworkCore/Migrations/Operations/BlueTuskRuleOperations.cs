using BlueTusk.EntityFrameworkCore.Rules;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskRuleOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required BlueTuskRuleDefinition Definition { get; init; }
    public bool OrReplace { get; init; }
}

public sealed class DropBlueTuskRuleOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
}

public sealed class RenameBlueTuskRuleOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class AlterBlueTuskRuleEnabledModeOperation : MigrationOperation
{
    public required string Table { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public required BlueTuskRuleEnabledMode EnabledMode { get; init; }
}
