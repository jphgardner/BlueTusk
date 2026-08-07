using BlueTusk.EntityFrameworkCore.Publications;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreatePublicationOperation : MigrationOperation
{
    public required BlueTuskPublicationDefinition Definition { get; init; }
}

public sealed class AlterPublicationOperation : MigrationOperation
{
    public required BlueTuskPublicationDefinition OldDefinition { get; init; }
    public required BlueTuskPublicationDefinition Definition { get; init; }
}

public sealed class DropPublicationOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class RenamePublicationOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}
