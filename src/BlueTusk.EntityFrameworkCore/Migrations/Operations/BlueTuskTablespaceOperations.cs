using BlueTusk.EntityFrameworkCore.Tablespaces;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateTablespaceOperation : MigrationOperation
{
    public required BlueTuskTablespaceDefinition Definition { get; init; }
}

public sealed class AlterTablespaceOperation : MigrationOperation
{
    public required BlueTuskTablespaceDefinition Definition { get; init; }

    public required BlueTuskTablespaceDefinition OldDefinition { get; init; }
}

public sealed class RenameTablespaceOperation : MigrationOperation
{
    public required string Name { get; init; }

    public required string NewName { get; init; }
}

public sealed class DropTablespaceOperation : MigrationOperation
{
    public required string Name { get; init; }

    public bool IfExists { get; init; }
}
