using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskOperatorOperation : MigrationOperation
{
    public required BlueTuskOperatorDefinition Definition { get; init; }
}

public sealed class ReplaceBlueTuskOperatorOperation : MigrationOperation
{
    public required BlueTuskOperatorDefinition OldDefinition { get; init; }
    public required BlueTuskOperatorDefinition Definition { get; init; }
}

public sealed class DropBlueTuskOperatorOperation : MigrationOperation
{
    public required BlueTuskOperatorDefinition Definition { get; init; }
}

public sealed class CreateBlueTuskOperatorFamilyOperation : MigrationOperation
{
    public required BlueTuskOperatorFamilyDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskOperatorFamilyOperation : MigrationOperation
{
    public required BlueTuskOperatorFamilyDefinition OldDefinition { get; init; }
    public required BlueTuskOperatorFamilyDefinition Definition { get; init; }
}

public sealed class DropBlueTuskOperatorFamilyOperation : MigrationOperation
{
    public required BlueTuskOperatorFamilyDefinition Definition { get; init; }
}

public sealed class CreateBlueTuskOperatorClassOperation : MigrationOperation
{
    public required BlueTuskOperatorClassDefinition Definition { get; init; }
}

public sealed class ReplaceBlueTuskOperatorClassOperation : MigrationOperation
{
    public required BlueTuskOperatorClassDefinition OldDefinition { get; init; }
    public required BlueTuskOperatorClassDefinition Definition { get; init; }
}

public sealed class DropBlueTuskOperatorClassOperation : MigrationOperation
{
    public required BlueTuskOperatorClassDefinition Definition { get; init; }
}

public sealed class CreateBlueTuskCastOperation : MigrationOperation
{
    public required BlueTuskCastDefinition Definition { get; init; }
}

public sealed class ReplaceBlueTuskCastOperation : MigrationOperation
{
    public required BlueTuskCastDefinition OldDefinition { get; init; }
    public required BlueTuskCastDefinition Definition { get; init; }
}

public sealed class DropBlueTuskCastOperation : MigrationOperation
{
    public required BlueTuskCastDefinition Definition { get; init; }
}

public sealed class CreateBlueTuskAggregateOperation : MigrationOperation
{
    public required BlueTuskAggregateDefinition Definition { get; init; }
}

public sealed class ReplaceBlueTuskAggregateOperation : MigrationOperation
{
    public required BlueTuskAggregateDefinition OldDefinition { get; init; }
    public required BlueTuskAggregateDefinition Definition { get; init; }
}

public sealed class DropBlueTuskAggregateOperation : MigrationOperation
{
    public required BlueTuskAggregateDefinition Definition { get; init; }
}
