using BlueTusk.EntityFrameworkCore.Routines;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineDefinition Definition { get; init; }
}

public sealed class ReplaceBlueTuskRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineDefinition OldDefinition { get; init; }

    public required BlueTuskRoutineDefinition Definition { get; init; }
}

public sealed class DropBlueTuskRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string IdentityArgumentsSql { get; init; }
}

public sealed class RenameBlueTuskRoutineOperation : MigrationOperation
{
    public required BlueTuskRoutineKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string IdentityArgumentsSql { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}
