using BlueTusk.EntityFrameworkCore.Triggers;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskTriggerOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required BlueTuskTriggerDefinition Definition { get; init; }

    public bool OrReplace { get; init; }
}

public sealed class DropBlueTuskTriggerOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }
}

public sealed class RenameBlueTuskTriggerOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required string NewName { get; init; }
}

public sealed class AlterBlueTuskTriggerEnabledModeOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required BlueTuskTriggerEnabledMode EnabledMode { get; init; }
}
