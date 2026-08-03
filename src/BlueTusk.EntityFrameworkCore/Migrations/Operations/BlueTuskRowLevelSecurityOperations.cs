using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

/// <summary>Creates a PostgreSQL row-level security policy.</summary>
public sealed class CreateBlueTuskRowSecurityPolicyOperation : MigrationOperation
{
    public string Table { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public BlueTuskRowSecurityPolicyDefinition Definition { get; set; } = null!;
}

/// <summary>Changes the roles or expressions of a PostgreSQL row-level security policy.</summary>
public sealed class AlterBlueTuskRowSecurityPolicyOperation : MigrationOperation
{
    public string Table { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public BlueTuskRowSecurityPolicyDefinition Definition { get; set; } = null!;
}

/// <summary>Drops a PostgreSQL row-level security policy.</summary>
public sealed class DropBlueTuskRowSecurityPolicyOperation : MigrationOperation
{
    public string Table { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Renames a PostgreSQL row-level security policy.</summary>
public sealed class RenameBlueTuskRowSecurityPolicyOperation : MigrationOperation
{
    public string Table { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NewName { get; set; } = string.Empty;
}

/// <summary>Changes row-level security enablement or owner enforcement for a PostgreSQL table.</summary>
public sealed class AlterBlueTuskRowLevelSecurityOperation : MigrationOperation
{
    public string Table { get; set; } = string.Empty;

    public string? Schema { get; set; }

    public bool? Enabled { get; set; }

    public bool? Forced { get; set; }
}
