using BlueTusk.EntityFrameworkCore.Views;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateViewOperation : MigrationOperation
{
    public required BlueTuskViewDefinition Definition { get; init; }
}

public sealed class ReplaceViewOperation : MigrationOperation
{
    public required BlueTuskViewDefinition OldDefinition { get; init; }

    public required BlueTuskViewDefinition Definition { get; init; }
}

public sealed class CreateMaterializedViewOperation : MigrationOperation
{
    public required BlueTuskMaterializedViewDefinition Definition { get; init; }
}

public sealed class AlterMaterializedViewOperation : MigrationOperation
{
    public required BlueTuskMaterializedViewDefinition OldDefinition { get; init; }

    public required BlueTuskMaterializedViewDefinition Definition { get; init; }
}

public sealed class DropViewOperation : MigrationOperation
{
    public required BlueTuskViewKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class RenameViewOperation : MigrationOperation
{
    public required BlueTuskViewKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}

public sealed class RefreshMaterializedViewOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public bool Concurrently { get; init; }

    public bool WithData { get; init; } = true;
}
