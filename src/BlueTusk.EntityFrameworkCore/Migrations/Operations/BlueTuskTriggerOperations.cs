using BlueTusk.EntityFrameworkCore.Triggers;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateTriggerOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required BlueTuskTriggerDefinition Definition { get; init; }

    public bool OrReplace { get; init; }
}

public sealed class DropTriggerOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }
}

public sealed class RenameTriggerOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required string NewName { get; init; }
}

public sealed class AlterTriggerEnabledModeOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required BlueTuskTriggerEnabledMode EnabledMode { get; init; }
}
