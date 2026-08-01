using BlueTusk.EntityFrameworkCore.EventTriggers;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskEventTriggerOperation : MigrationOperation
{
    public required BlueTuskEventTriggerDefinition Definition { get; init; }
}

public sealed class DropBlueTuskEventTriggerOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameBlueTuskEventTriggerOperation : MigrationOperation
{
    public required string Name { get; init; }

    public required string NewName { get; init; }
}

public sealed class AlterBlueTuskEventTriggerEnabledModeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public required BlueTuskEventTriggerEnabledMode EnabledMode { get; init; }
}
