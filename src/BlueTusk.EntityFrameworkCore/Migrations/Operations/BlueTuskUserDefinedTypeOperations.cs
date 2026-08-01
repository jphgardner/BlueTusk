using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskEnumTypeOperation : MigrationOperation
{
    public required BlueTuskEnumTypeDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskEnumTypeOperation : MigrationOperation
{
    public required BlueTuskEnumTypeDefinition OldDefinition { get; init; }

    public required BlueTuskEnumTypeDefinition Definition { get; init; }
}

public sealed class DropBlueTuskEnumTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class CreateBlueTuskDomainTypeOperation : MigrationOperation
{
    public required BlueTuskDomainTypeDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskDomainTypeOperation : MigrationOperation
{
    public required BlueTuskDomainTypeDefinition OldDefinition { get; init; }

    public required BlueTuskDomainTypeDefinition Definition { get; init; }
}

public sealed class DropBlueTuskDomainTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class CreateBlueTuskCompositeTypeOperation : MigrationOperation
{
    public required BlueTuskCompositeTypeDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskCompositeTypeOperation : MigrationOperation
{
    public required BlueTuskCompositeTypeDefinition OldDefinition { get; init; }

    public required BlueTuskCompositeTypeDefinition Definition { get; init; }
}

public sealed class DropBlueTuskCompositeTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class CreateBlueTuskRangeTypeOperation : MigrationOperation
{
    public required BlueTuskRangeTypeDefinition Definition { get; init; }
}

public sealed class DropBlueTuskRangeTypeOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }
}

public sealed class RenameBlueTuskRangeTypeOperation : MigrationOperation
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

public sealed class RenameBlueTuskUserDefinedTypeOperation : MigrationOperation
{
    public required BlueTuskUserDefinedTypeKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }

    public string? NewSchema { get; init; }
}
