using BlueTusk.EntityFrameworkCore.EventTriggers;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateEventTriggerOperation : MigrationOperation
{
    public required BlueTuskEventTriggerDefinition Definition { get; init; }
}

public sealed class DropEventTriggerOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameEventTriggerOperation : MigrationOperation
{
    public required string Name { get; init; }

    public required string NewName { get; init; }
}

public sealed class AlterEventTriggerEnabledModeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public required BlueTuskEventTriggerEnabledMode EnabledMode { get; init; }
}
