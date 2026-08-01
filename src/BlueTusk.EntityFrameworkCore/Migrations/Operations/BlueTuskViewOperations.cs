using BlueTusk.EntityFrameworkCore.Views;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskViewOperation : MigrationOperation
{
    public required BlueTuskViewDefinition Definition { get; init; }
}

public sealed class ReplaceBlueTuskViewOperation : MigrationOperation
{
    public required BlueTuskViewDefinition OldDefinition { get; init; }

    public required BlueTuskViewDefinition Definition { get; init; }
}

public sealed class CreateBlueTuskMaterializedViewOperation : MigrationOperation
{
    public required BlueTuskMaterializedViewDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskMaterializedViewOperation : MigrationOperation
{
    public required BlueTuskMaterializedViewDefinition OldDefinition { get; init; }

    public required BlueTuskMaterializedViewDefinition Definition { get; init; }
}

public sealed class DropBlueTuskViewOperation : MigrationOperation
{
    public required BlueTuskViewKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class RenameBlueTuskViewOperation : MigrationOperation
{
    public required BlueTuskViewKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}

public sealed class RefreshBlueTuskMaterializedViewOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public bool Concurrently { get; init; }

    public bool WithData { get; init; } = true;
}
