using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateEnumTypeOperation : MigrationOperation
{
    public required BlueTuskEnumTypeDefinition Definition { get; init; }
}

public sealed class AlterEnumTypeOperation : MigrationOperation
{
    public required BlueTuskEnumTypeDefinition OldDefinition { get; init; }

    public required BlueTuskEnumTypeDefinition Definition { get; init; }
}

public sealed class DropEnumTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class CreateDomainTypeOperation : MigrationOperation
{
    public required BlueTuskDomainTypeDefinition Definition { get; init; }
}

public sealed class AlterDomainTypeOperation : MigrationOperation
{
    public required BlueTuskDomainTypeDefinition OldDefinition { get; init; }

    public required BlueTuskDomainTypeDefinition Definition { get; init; }
}

public sealed class DropDomainTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class CreateCompositeTypeOperation : MigrationOperation
{
    public required BlueTuskCompositeTypeDefinition Definition { get; init; }
}

public sealed class AlterCompositeTypeOperation : MigrationOperation
{
    public required BlueTuskCompositeTypeDefinition OldDefinition { get; init; }

    public required BlueTuskCompositeTypeDefinition Definition { get; init; }
}

public sealed class DropCompositeTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class CreateRangeTypeOperation : MigrationOperation
{
    public required BlueTuskRangeTypeDefinition Definition { get; init; }
}

public sealed class DropRangeTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class RenameRangeTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }

    public required string MultirangeName { get; init; }

    public string? MultirangeSchema { get; init; }

    public required string NewMultirangeName { get; init; }

    public string? NewMultirangeSchema { get; init; }
}

public sealed class RenameUserDefinedTypeOperation : MigrationOperation
{
    public required BlueTuskUserDefinedTypeKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}
