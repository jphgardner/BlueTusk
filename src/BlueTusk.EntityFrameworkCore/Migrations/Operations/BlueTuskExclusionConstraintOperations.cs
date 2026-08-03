using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class AddBlueTuskExclusionConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required BlueTuskExclusionConstraintDefinition Definition { get; init; }
}

public sealed class DropBlueTuskExclusionConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }
}

public sealed class RenameBlueTuskExclusionConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required string NewName { get; init; }
}
