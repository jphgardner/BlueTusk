using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateOperatorOperation : MigrationOperation
{
    public required BlueTuskOperatorDefinition Definition { get; init; }
}

public sealed class ReplaceOperatorOperation : MigrationOperation
{
    public required BlueTuskOperatorDefinition OldDefinition { get; init; }
    public required BlueTuskOperatorDefinition Definition { get; init; }
}

public sealed class DropOperatorOperation : MigrationOperation
{
    public required BlueTuskOperatorDefinition Definition { get; init; }
}

public sealed class CreateOperatorFamilyOperation : MigrationOperation
{
    public required BlueTuskOperatorFamilyDefinition Definition { get; init; }
}

public sealed class AlterOperatorFamilyOperation : MigrationOperation
{
    public required BlueTuskOperatorFamilyDefinition OldDefinition { get; init; }
    public required BlueTuskOperatorFamilyDefinition Definition { get; init; }
}

public sealed class DropOperatorFamilyOperation : MigrationOperation
{
    public required BlueTuskOperatorFamilyDefinition Definition { get; init; }
}

public sealed class CreateOperatorClassOperation : MigrationOperation
{
    public required BlueTuskOperatorClassDefinition Definition { get; init; }
}

public sealed class ReplaceOperatorClassOperation : MigrationOperation
{
    public required BlueTuskOperatorClassDefinition OldDefinition { get; init; }
    public required BlueTuskOperatorClassDefinition Definition { get; init; }
}

public sealed class DropOperatorClassOperation : MigrationOperation
{
    public required BlueTuskOperatorClassDefinition Definition { get; init; }
}

public sealed class CreateCastOperation : MigrationOperation
{
    public required BlueTuskCastDefinition Definition { get; init; }
}

public sealed class ReplaceCastOperation : MigrationOperation
{
    public required BlueTuskCastDefinition OldDefinition { get; init; }
    public required BlueTuskCastDefinition Definition { get; init; }
}

public sealed class DropCastOperation : MigrationOperation
{
    public required BlueTuskCastDefinition Definition { get; init; }
}

public sealed class CreateAggregateOperation : MigrationOperation
{
    public required BlueTuskAggregateDefinition Definition { get; init; }
}

public sealed class ReplaceAggregateOperation : MigrationOperation
{
    public required BlueTuskAggregateDefinition OldDefinition { get; init; }
    public required BlueTuskAggregateDefinition Definition { get; init; }
}

public sealed class DropAggregateOperation : MigrationOperation
{
    public required BlueTuskAggregateDefinition Definition { get; init; }
}
