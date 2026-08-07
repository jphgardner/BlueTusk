using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class AddExclusionConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required BlueTuskExclusionConstraintDefinition Definition { get; init; }
}

public sealed class DropExclusionConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }
}

public sealed class RenameExclusionConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required string NewName { get; init; }
}
