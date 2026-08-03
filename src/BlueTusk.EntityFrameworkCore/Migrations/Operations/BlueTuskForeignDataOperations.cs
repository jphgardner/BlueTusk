using BlueTusk.EntityFrameworkCore.ForeignData;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateForeignDataWrapperOperation : MigrationOperation
{
    public required BlueTuskForeignDataWrapperDefinition Definition { get; init; }
}

public sealed class AlterForeignDataWrapperOperation : MigrationOperation
{
    public required BlueTuskForeignDataWrapperDefinition OldDefinition { get; init; }
    public required BlueTuskForeignDataWrapperDefinition Definition { get; init; }
}

public sealed class DropForeignDataWrapperOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameForeignDataWrapperOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class CreateForeignServerOperation : MigrationOperation
{
    public required BlueTuskForeignServerDefinition Definition { get; init; }
}

public sealed class AlterForeignServerOperation : MigrationOperation
{
    public required BlueTuskForeignServerDefinition OldDefinition { get; init; }
    public required BlueTuskForeignServerDefinition Definition { get; init; }
}

public sealed class DropForeignServerOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameForeignServerOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class CreateUserMappingOperation : MigrationOperation
{
    public required BlueTuskUserMappingDefinition Definition { get; init; }
}

public sealed class AlterUserMappingOperation : MigrationOperation
{
    public required BlueTuskUserMappingDefinition OldDefinition { get; init; }
    public required BlueTuskUserMappingDefinition Definition { get; init; }
}

public sealed class DropUserMappingOperation : MigrationOperation
{
    public required string ServerName { get; init; }
    public string? UserName { get; init; }
}
