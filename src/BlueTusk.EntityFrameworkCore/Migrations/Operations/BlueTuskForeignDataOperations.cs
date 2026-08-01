using BlueTusk.EntityFrameworkCore.ForeignData;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskForeignDataWrapperOperation : MigrationOperation
{
    public required BlueTuskForeignDataWrapperDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskForeignDataWrapperOperation : MigrationOperation
{
    public required BlueTuskForeignDataWrapperDefinition OldDefinition { get; init; }
    public required BlueTuskForeignDataWrapperDefinition Definition { get; init; }
}

public sealed class DropBlueTuskForeignDataWrapperOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameBlueTuskForeignDataWrapperOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class CreateBlueTuskForeignServerOperation : MigrationOperation
{
    public required BlueTuskForeignServerDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskForeignServerOperation : MigrationOperation
{
    public required BlueTuskForeignServerDefinition OldDefinition { get; init; }
    public required BlueTuskForeignServerDefinition Definition { get; init; }
}

public sealed class DropBlueTuskForeignServerOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameBlueTuskForeignServerOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class CreateBlueTuskUserMappingOperation : MigrationOperation
{
    public required BlueTuskUserMappingDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskUserMappingOperation : MigrationOperation
{
    public required BlueTuskUserMappingDefinition OldDefinition { get; init; }
    public required BlueTuskUserMappingDefinition Definition { get; init; }
}

public sealed class DropBlueTuskUserMappingOperation : MigrationOperation
{
    public required string ServerName { get; init; }
    public string? UserName { get; init; }
}
