using BlueTusk.EntityFrameworkCore.Extensions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateExtensionOperation : MigrationOperation
{
    public required BlueTuskExtensionDefinition Definition { get; init; }

    public bool IfNotExists { get; init; }
}

public sealed class AlterExtensionOperation : MigrationOperation
{
    public required BlueTuskExtensionDefinition OldDefinition { get; init; }

    public required BlueTuskExtensionDefinition Definition { get; init; }
}

public sealed class DropExtensionOperation : MigrationOperation
{
    public required string Name { get; init; }

    public bool IfExists { get; init; }

    public bool Cascade { get; init; }
}
