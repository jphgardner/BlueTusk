using BlueTusk.EntityFrameworkCore.Partitioning;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

/// <summary>Creates a PostgreSQL table as a partition of another table.</summary>
public sealed class CreatePartitionOperation : MigrationOperation
{
    public string ParentName { get; set; } = string.Empty;

    public string? ParentSchema { get; set; }

    public BlueTuskPartitionDefinition Definition { get; set; } = null!;
}

/// <summary>Drops a PostgreSQL partition table.</summary>
public sealed class DropPartitionOperation : MigrationOperation
{
    public string Name { get; set; } = string.Empty;

    public string? Schema { get; set; }
}

/// <summary>Renames or moves a PostgreSQL partition table.</summary>
public sealed class AlterPartitionOperation : MigrationOperation
{
    public string Name { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public string NewName { get; set; } = string.Empty;

    public string? NewSchema { get; set; }
}

/// <summary>Attaches an existing table to a PostgreSQL partitioned table.</summary>
public sealed class AttachPartitionOperation : MigrationOperation
{
    public string ParentName { get; set; } = string.Empty;

    public string? ParentSchema { get; set; }

    public string PartitionName { get; set; } = string.Empty;

    public string? PartitionSchema { get; set; }

    public BlueTuskPartitionBound Bound { get; set; } = null!;
}

/// <summary>Controls PostgreSQL partition detachment locking/recovery behavior.</summary>
public enum BlueTuskPartitionDetachMode
{
    /// <summary>Detaches in the current migration transaction.</summary>
    Normal,

    /// <summary>Uses PostgreSQL's reduced-lock, two-transaction detach.</summary>
    Concurrently,

    /// <summary>Finalizes a previously interrupted concurrent detach.</summary>
    Finalize,
}

/// <summary>Detaches a PostgreSQL partition into a standalone table.</summary>
public sealed class DetachPartitionOperation : MigrationOperation
{
    public string ParentName { get; set; } = string.Empty;

    public string? ParentSchema { get; set; }

    public string PartitionName { get; set; } = string.Empty;

    public string? PartitionSchema { get; set; }

    public BlueTuskPartitionDetachMode Mode { get; set; }
}
