using BlueTusk.EntityFrameworkCore.Collations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskCollationOperation : MigrationOperation
{
    public required BlueTuskCollationDefinition Definition { get; init; }

    public bool IfNotExists { get; init; }
}

public sealed class CreateBlueTuskCollationFromOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string SourceName { get; init; }

    public string? SourceSchema { get; init; }

    public bool IfNotExists { get; init; }
}

public sealed class RenameBlueTuskCollationOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}

public sealed class RefreshBlueTuskCollationVersionOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class DropBlueTuskCollationOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public bool IfExists { get; init; }

    public bool Cascade { get; init; }
}
