using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

/// <summary>Creates a PostgreSQL property graph.</summary>
public sealed class CreatePropertyGraphOperation : MigrationOperation
{
    public BlueTuskPropertyGraphDefinition Definition { get; set; } = null!;
}

/// <summary>Drops a PostgreSQL property graph.</summary>
public sealed class DropPropertyGraphOperation : MigrationOperation
{
    public string Name { get; set; } = string.Empty;

    public string? Schema { get; set; }
}

/// <summary>Renames a PostgreSQL property graph and/or moves it to another schema.</summary>
public sealed class AlterPropertyGraphOperation : MigrationOperation
{
    public string Name { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public string NewName { get; set; } = string.Empty;

    public string? NewSchema { get; set; }
}
