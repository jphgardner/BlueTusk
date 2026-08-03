namespace BlueTusk.EntityFrameworkCore.RowLevelSecurity;

/// <summary>How a PostgreSQL row-security policy combines with other applicable policies.</summary>
public enum BlueTuskRowSecurityPolicyBehavior
{
    /// <summary>Combines with other permissive policies using <c>OR</c>.</summary>
    Permissive,

    /// <summary>Combines with other restrictive policies using <c>AND</c>.</summary>
    Restrictive,
}

/// <summary>The PostgreSQL command to which a row-security policy applies.</summary>
public enum BlueTuskRowSecurityPolicyCommand
{
    /// <summary>All commands.</summary>
    All,

    /// <summary><c>SELECT</c> commands.</summary>
    Select,

    /// <summary><c>INSERT</c> commands.</summary>
    Insert,

    /// <summary><c>UPDATE</c> commands.</summary>
    Update,

    /// <summary><c>DELETE</c> commands.</summary>
    Delete,
}

/// <summary>The kind of a role target in a PostgreSQL row-security policy.</summary>
public enum BlueTuskRowSecurityRoleKind
{
    /// <summary>A named database role.</summary>
    Named,

    /// <summary>The PostgreSQL <c>PUBLIC</c> pseudo-role.</summary>
    Public,

    /// <summary>The PostgreSQL <c>CURRENT_ROLE</c> pseudo-role.</summary>
    CurrentRole,

    /// <summary>The PostgreSQL <c>CURRENT_USER</c> pseudo-role.</summary>
    CurrentUser,

    /// <summary>The PostgreSQL <c>SESSION_USER</c> pseudo-role.</summary>
    SessionUser,
}

/// <summary>A role target for a PostgreSQL row-security policy.</summary>
public sealed record BlueTuskRowSecurityRoleDefinition(
    BlueTuskRowSecurityRoleKind Kind,
    string? Name = null)
{
    /// <summary>Creates a named database-role target.</summary>
    public static BlueTuskRowSecurityRoleDefinition Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new BlueTuskRowSecurityRoleDefinition(BlueTuskRowSecurityRoleKind.Named, name);
    }

    /// <summary>The PostgreSQL <c>PUBLIC</c> pseudo-role.</summary>
    public static BlueTuskRowSecurityRoleDefinition Public { get; } =
        new(BlueTuskRowSecurityRoleKind.Public);

    /// <summary>The PostgreSQL <c>CURRENT_ROLE</c> pseudo-role.</summary>
    public static BlueTuskRowSecurityRoleDefinition CurrentRole { get; } =
        new(BlueTuskRowSecurityRoleKind.CurrentRole);

    /// <summary>The PostgreSQL <c>CURRENT_USER</c> pseudo-role.</summary>
    public static BlueTuskRowSecurityRoleDefinition CurrentUser { get; } =
        new(BlueTuskRowSecurityRoleKind.CurrentUser);

    /// <summary>The PostgreSQL <c>SESSION_USER</c> pseudo-role.</summary>
    public static BlueTuskRowSecurityRoleDefinition SessionUser { get; } =
        new(BlueTuskRowSecurityRoleKind.SessionUser);
}

/// <summary>A PostgreSQL row-level security policy.</summary>
public sealed record BlueTuskRowSecurityPolicyDefinition(
    string Name,
    BlueTuskRowSecurityPolicyBehavior Behavior,
    BlueTuskRowSecurityPolicyCommand Command,
    IReadOnlyList<BlueTuskRowSecurityRoleDefinition> Roles,
    string? UsingSql = null,
    string? WithCheckSql = null);

/// <summary>Row-level security metadata for one PostgreSQL table.</summary>
public sealed record BlueTuskRowLevelSecurityDefinition(
    bool Enabled,
    bool Forced,
    IReadOnlyList<BlueTuskRowSecurityPolicyDefinition> Policies);

/// <summary>A named PostgreSQL table and its row-level security metadata.</summary>
public sealed record BlueTuskRowLevelSecurityTableDefinition(
    string Name,
    string? Schema,
    BlueTuskRowLevelSecurityDefinition RowLevelSecurity);
