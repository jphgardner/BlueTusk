using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

/// <summary>Adds a direct PostgreSQL table-inheritance relationship.</summary>
public sealed class AddBlueTuskTableInheritanceOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string ParentTable { get; init; }

    public string? ParentSchema { get; init; }
}

/// <summary>Removes a direct PostgreSQL table-inheritance relationship.</summary>
public sealed class RemoveBlueTuskTableInheritanceOperation : MigrationOperation
{
    public required string Table { get; init; }

    public string? Schema { get; init; }

    public required string ParentTable { get; init; }

    public string? ParentSchema { get; init; }
}
