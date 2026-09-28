using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Schema;

public readonly record struct SchemaRelationIdentity(string Schema, string Name);

public sealed record SchemaColumn(
    string Name,
    int Ordinal,
    string PostgreSqlType,
    bool IsNullable,
    string? DefaultSql = null,
    string IdentityKind = "",
    string GeneratedKind = "",
    string? Collation = null);

public sealed record SchemaConstraint(string Name, string Kind, string Definition);

public sealed record SchemaIndex(string Name, string Definition, bool IsValid);

public sealed record SchemaPolicy(string Name, string Command, bool IsPermissive, string Roles, string? UsingSql, string? CheckSql);

public sealed class SchemaRelation
{
    public SchemaRelation(
        SchemaRelationIdentity identity,
        string kind,
        bool rowSecurity,
        bool forceRowSecurity,
        string replicaIdentity,
        IEnumerable<SchemaColumn> columns,
        IEnumerable<SchemaConstraint>? constraints = null,
        IEnumerable<SchemaIndex>? indexes = null,
        IEnumerable<SchemaPolicy>? policies = null,
        string? definitionSql = null,
        SchemaSnapshotLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        limits ??= SchemaSnapshotLimits.Default;
        limits.Validate();
        var budget = new SchemaModelBudget(limits);
        budget.Text(identity.Schema, required: true, identifier: true);
        budget.Text(identity.Name, required: true, identifier: true);
        if (kind is not ("r" or "p" or "v" or "m" or "f") || replicaIdentity is not ("d" or "n" or "f" or "i"))
        {
            throw new ArgumentException("Unsupported PostgreSQL relation or replica identity flag.");
        }
        budget.Text(definitionSql);
        Identity = identity;
        Kind = kind;
        RowSecurity = rowSecurity;
        ForceRowSecurity = forceRowSecurity;
        ReplicaIdentity = replicaIdentity;
        var columnList = SchemaModelBudget.Materialize(columns, limits.MaximumColumns, column =>
        {
            budget.Add(64);
            budget.Text(column.Name, required: true, identifier: true);
            budget.Text(column.PostgreSqlType, required: true);
            budget.Text(column.DefaultSql);
            budget.Text(column.Collation);
            if (column.Ordinal is < 1 or > short.MaxValue || column.IdentityKind is not ("" or "a" or "d") ||
                column.GeneratedKind is not ("" or "s" or "v"))
            {
                throw new ArgumentException("Invalid PostgreSQL column ordinal, identity or generated flag.");
            }
        });
        var constraintList = SchemaModelBudget.Materialize(constraints ?? [], limits.MaximumConstraints, item =>
        {
            budget.Add(32);
            budget.Text(item.Name, required: true, identifier: true);
            budget.Text(item.Definition, required: true);
            if (item.Kind is not ("c" or "f" or "n" or "p" or "u" or "t" or "x"))
            {
                throw new ArgumentException("Invalid PostgreSQL constraint kind.");
            }
        });
        var indexList = SchemaModelBudget.Materialize(indexes ?? [], limits.MaximumIndexes, item =>
        {
            budget.Add(32);
            budget.Text(item.Name, required: true, identifier: true);
            budget.Text(item.Definition, required: true);
        });
        var policyList = SchemaModelBudget.Materialize(policies ?? [], limits.MaximumPolicies, item =>
        {
            budget.Add(64);
            budget.Text(item.Name, required: true, identifier: true);
            budget.Text(item.Roles, required: true);
            budget.Text(item.UsingSql);
            budget.Text(item.CheckSql);
            if (item.Command is not ("*" or "r" or "a" or "w" or "d")) { throw new ArgumentException("Invalid PostgreSQL policy command."); }
        });
        Columns = Array.AsReadOnly(columnList.OrderBy(column => column.Ordinal).ToArray());
        Constraints = Array.AsReadOnly(constraintList.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
        Indexes = Array.AsReadOnly(indexList.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
        Policies = Array.AsReadOnly(policyList.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
        MetadataBytes = budget.UsedBytes + 128;
        DefinitionSql = definitionSql;
        if (Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != Columns.Count ||
            Columns.Select(column => column.Ordinal).Distinct().Count() != Columns.Count ||
            Constraints.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Constraints.Count ||
            Indexes.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Indexes.Count ||
            Policies.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Policies.Count)
        {
            throw new ArgumentException("Schema members must have distinct identities and valid column ordinals.", nameof(columns));
        }
    }

    public SchemaRelationIdentity Identity { get; }
    public string Kind { get; }
    public bool RowSecurity { get; }
    public bool ForceRowSecurity { get; }
    public string ReplicaIdentity { get; }
    public ReadOnlyCollection<SchemaColumn> Columns { get; }
    public ReadOnlyCollection<SchemaConstraint> Constraints { get; }
    public ReadOnlyCollection<SchemaIndex> Indexes { get; }
    public ReadOnlyCollection<SchemaPolicy> Policies { get; }
    public string? DefinitionSql { get; }
    internal long MetadataBytes { get; }
}

public sealed class SchemaSnapshot
{
    public const int CurrentFormatVersion = 1;

    public SchemaSnapshot(IEnumerable<SchemaRelation> relations, SchemaSnapshotLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(relations);
        limits ??= SchemaSnapshotLimits.Default;
        limits.Validate();
        var budget = new SchemaModelBudget(limits);
        long columns = 0, constraints = 0, indexes = 0, policies = 0;
        var relationList = SchemaModelBudget.Materialize(relations, limits.MaximumRelations, relation =>
        {
            budget.Add(relation.MetadataBytes);
            columns += relation.Columns.Count;
            constraints += relation.Constraints.Count;
            indexes += relation.Indexes.Count;
            policies += relation.Policies.Count;
            if (columns > limits.MaximumColumns || constraints > limits.MaximumConstraints ||
                indexes > limits.MaximumIndexes || policies > limits.MaximumPolicies) { throw new SchemaCaptureLimitException(); }
        });
        Relations = Array.AsReadOnly(relationList.OrderBy(relation => relation.Identity.Schema, StringComparer.Ordinal)
            .ThenBy(relation => relation.Identity.Name, StringComparer.Ordinal).ToArray());
        if (Relations.Select(relation => relation.Identity).Distinct().Count() != Relations.Count)
        {
            throw new ArgumentException("Schema relation identities must be unique.", nameof(relations));
        }

        Fingerprint = CalculateFingerprint();
    }

    public ReadOnlyCollection<SchemaRelation> Relations { get; }
    public string Fingerprint { get; }

    private string CalculateFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "BlueTusk.Schema:1");
        Append(hash, Relations.Count);
        foreach (var relation in Relations)
        {
            Append(hash, relation.Identity.Schema);
            Append(hash, relation.Identity.Name);
            Append(hash, relation.Kind);
            Append(hash, relation.RowSecurity ? 1 : 0);
            Append(hash, relation.ForceRowSecurity ? 1 : 0);
            Append(hash, relation.ReplicaIdentity);
            Append(hash, relation.DefinitionSql);
            Append(hash, relation.Columns.Count);
            foreach (var column in relation.Columns)
            {
                Append(hash, column.Name);
                Append(hash, column.Ordinal);
                Append(hash, column.PostgreSqlType);
                Append(hash, column.IsNullable ? 1 : 0);
                Append(hash, column.DefaultSql);
                Append(hash, column.IdentityKind);
                Append(hash, column.GeneratedKind);
                Append(hash, column.Collation);
            }

            Append(hash, relation.Constraints.Count);
            foreach (var constraint in relation.Constraints)
            {
                Append(hash, constraint.Name);
                Append(hash, constraint.Kind);
                Append(hash, constraint.Definition);
            }

            Append(hash, relation.Indexes.Count);
            foreach (var index in relation.Indexes)
            {
                Append(hash, index.Name);
                Append(hash, index.Definition);
                Append(hash, index.IsValid ? 1 : 0);
            }

            Append(hash, relation.Policies.Count);
            foreach (var policy in relation.Policies)
            {
                Append(hash, policy.Name);
                Append(hash, policy.Command);
                Append(hash, policy.IsPermissive ? 1 : 0);
                Append(hash, policy.Roles);
                Append(hash, policy.UsingSql);
                Append(hash, policy.CheckSql);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Append(hash, -1);
            return;
        }

        var length = Encoding.UTF8.GetByteCount(value);
        Append(hash, length);
        byte[]? rented = null;
        Span<byte> buffer = length <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
