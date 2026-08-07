using BlueTusk.EntityFrameworkCore.ExpressionIndexes;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateExpressionIndexOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required BlueTuskExpressionIndexDefinition Definition { get; init; }
}

public sealed class DropExpressionIndexOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public bool Concurrently { get; init; }
}

public sealed class RenameExpressionIndexOperation : MigrationOperation
{
    public required string Name { get; init; }

    public string? Schema { get; init; }

    public required string NewName { get; init; }
}
