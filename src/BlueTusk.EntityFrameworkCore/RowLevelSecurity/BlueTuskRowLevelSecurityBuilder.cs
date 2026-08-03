using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.RowLevelSecurity;

/// <summary>Builds PostgreSQL row-level security metadata for an EF entity table.</summary>
public sealed class BlueTuskRowLevelSecurityBuilder
{
    private readonly IMutableEntityType _entityType;

    internal BlueTuskRowLevelSecurityBuilder(IMutableEntityType entityType) =>
        _entityType = entityType;

    /// <summary>Enables or disables application of policies for the table.</summary>
    public BlueTuskRowLevelSecurityBuilder IsEnabled(bool enabled = true) =>
        Update(definition => definition with { Enabled = enabled });

    /// <summary>Controls whether policies also apply to the table owner.</summary>
    public BlueTuskRowLevelSecurityBuilder IsForced(bool forced = true) =>
        Update(definition => definition with { Forced = forced });

    /// <summary>Adds or replaces a policy using fixed application-model SQL expressions.</summary>
    public BlueTuskRowLevelSecurityBuilder HasPolicy(
        string name,
        BlueTuskRowSecurityPolicyCommand command = BlueTuskRowSecurityPolicyCommand.All,
        BlueTuskRowSecurityPolicyBehavior behavior = BlueTuskRowSecurityPolicyBehavior.Permissive,
        string? usingSql = null,
        string? withCheckSql = null,
        params BlueTuskRowSecurityRoleDefinition[] roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roles);
        var policy = new BlueTuskRowSecurityPolicyDefinition(
            name,
            behavior,
            command,
            roles.Length == 0 ? [BlueTuskRowSecurityRoleDefinition.Public] : roles,
            usingSql,
            withCheckSql);
        ValidatePolicy(policy);
        return Update(definition => definition with
        {
            Policies = definition.Policies
                .Where(existing => !string.Equals(existing.Name, name, StringComparison.Ordinal))
                .Append(policy)
                .OrderBy(existing => existing.Name, StringComparer.Ordinal)
                .ToArray(),
        });
    }

    /// <summary>Removes a named policy.</summary>
    public BlueTuskRowLevelSecurityBuilder HasNoPolicy(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Update(definition => definition with
        {
            Policies = definition.Policies
                .Where(policy => !string.Equals(policy.Name, name, StringComparison.Ordinal))
                .ToArray(),
        });
    }

    internal static void ValidateDefinition(BlueTuskRowLevelSecurityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Policies);
        var duplicate = definition.Policies
            .GroupBy(policy => policy.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Row-security policy '{duplicate.Key}' is configured more than once.",
                nameof(definition));
        }

        foreach (var policy in definition.Policies)
        {
            ValidatePolicy(policy);
        }
    }

    internal static void ValidatePolicy(BlueTuskRowSecurityPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Name);
        ArgumentNullException.ThrowIfNull(policy.Roles);
        if (!Enum.IsDefined(policy.Behavior))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Behavior, "Unknown policy behavior.");
        }

        if (!Enum.IsDefined(policy.Command))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Command, "Unknown policy command.");
        }

        if (policy.Roles.Count == 0)
        {
            throw new ArgumentException("A row-security policy requires at least one role target.", nameof(policy));
        }

        foreach (var role in policy.Roles)
        {
            ArgumentNullException.ThrowIfNull(role);
            if (!Enum.IsDefined(role.Kind))
            {
                throw new ArgumentOutOfRangeException(nameof(policy), role.Kind, "Unknown policy role kind.");
            }

            if (role.Kind == BlueTuskRowSecurityRoleKind.Named)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(role.Name);
            }
            else if (role.Name is not null)
            {
                throw new ArgumentException("PostgreSQL pseudo-role targets cannot have a name.", nameof(policy));
            }
        }

        if (policy.Command == BlueTuskRowSecurityPolicyCommand.Insert && policy.UsingSql is not null)
        {
            throw new ArgumentException("PostgreSQL INSERT policies cannot have a USING expression.", nameof(policy));
        }

        if (policy.Command is BlueTuskRowSecurityPolicyCommand.Select or BlueTuskRowSecurityPolicyCommand.Delete &&
            policy.WithCheckSql is not null)
        {
            throw new ArgumentException(
                $"PostgreSQL {policy.Command.ToString().ToUpperInvariant()} policies cannot have a WITH CHECK expression.",
                nameof(policy));
        }

        if (policy.UsingSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policy.UsingSql);
        }

        if (policy.WithCheckSql is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policy.WithCheckSql);
        }
    }

    private BlueTuskRowLevelSecurityBuilder Update(
        Func<BlueTuskRowLevelSecurityDefinition, BlueTuskRowLevelSecurityDefinition> update)
    {
        var definition = BlueTuskRowLevelSecurityMetadata.Get(_entityType)
            ?? throw new InvalidOperationException("The entity does not have BlueTusk row-level security metadata.");
        var updated = update(definition);
        ValidateDefinition(updated);
        _entityType.SetAnnotation(
            BlueTuskRowLevelSecurityMetadata.AnnotationName,
            BlueTuskRowLevelSecurityMetadata.Serialize(updated));
        return this;
    }
}
