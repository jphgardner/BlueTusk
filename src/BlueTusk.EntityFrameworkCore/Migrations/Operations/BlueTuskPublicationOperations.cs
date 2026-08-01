using BlueTusk.EntityFrameworkCore.Publications;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskPublicationOperation : MigrationOperation
{
    public required BlueTuskPublicationDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskPublicationOperation : MigrationOperation
{
    public required BlueTuskPublicationDefinition OldDefinition { get; init; }
    public required BlueTuskPublicationDefinition Definition { get; init; }
}

public sealed class DropBlueTuskPublicationOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenameBlueTuskPublicationOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}
