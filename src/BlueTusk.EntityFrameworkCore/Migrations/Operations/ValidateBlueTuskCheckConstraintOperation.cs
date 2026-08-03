using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

/// <summary>Validates an existing PostgreSQL CHECK constraint against all rows.</summary>
public sealed class ValidateBlueTuskCheckConstraintOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }
}
