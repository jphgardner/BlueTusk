using BlueTusk.EntityFrameworkCore.Tablespaces;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskTablespaceOperation : MigrationOperation
{
    public required BlueTuskTablespaceDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskTablespaceOperation : MigrationOperation
{
    public required BlueTuskTablespaceDefinition Definition { get; init; }

    public required BlueTuskTablespaceDefinition OldDefinition { get; init; }
}

public sealed class RenameBlueTuskTablespaceOperation : MigrationOperation
{
    public required string Name { get; init; }

    public required string NewName { get; init; }
}

public sealed class DropBlueTuskTablespaceOperation : MigrationOperation
{
    public required string Name { get; init; }

    public bool IfExists { get; init; }
}
