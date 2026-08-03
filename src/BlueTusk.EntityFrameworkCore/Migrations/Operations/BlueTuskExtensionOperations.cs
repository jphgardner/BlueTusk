using BlueTusk.EntityFrameworkCore.Extensions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskExtensionOperation : MigrationOperation
{
    public required BlueTuskExtensionDefinition Definition { get; init; }

    public bool IfNotExists { get; init; }
}

public sealed class AlterBlueTuskExtensionOperation : MigrationOperation
{
    public required BlueTuskExtensionDefinition OldDefinition { get; init; }

    public required BlueTuskExtensionDefinition Definition { get; init; }
}

public sealed class DropBlueTuskExtensionOperation : MigrationOperation
{
    public required string Name { get; init; }

    public bool IfExists { get; init; }

    public bool Cascade { get; init; }
}
