using BlueTusk.EntityFrameworkCore.Routines;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineDefinition Definition { get; init; }
}

public sealed class ReplaceRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineDefinition OldDefinition { get; init; }

    public required BlueTuskRoutineDefinition Definition { get; init; }
}

public sealed class DropRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string IdentityArgumentsSql { get; init; }
}

public sealed class RenameRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string IdentityArgumentsSql { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}
