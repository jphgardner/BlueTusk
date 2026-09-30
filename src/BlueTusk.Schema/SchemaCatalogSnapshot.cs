using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Schema;

/// <summary>Bounds supplemental catalogue contracts independently of the relation snapshot.</summary>
public sealed record SchemaCatalogLimits
{
    public SchemaSnapshotLimits Relations { get; init; } = new();
    public int MaximumEntries { get; init; } = 100_000;
    public int MaximumEnumLabels { get; init; } = 200_000;
    public int MaximumDomainConstraints { get; init; } = 100_000;
    public int MaximumMetadataBytes { get; init; } = 64 * 1024 * 1024;
    internal static SchemaCatalogLimits Default { get; } = new();

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Relations);
        Relations.Validate();
        if (MaximumEntries is < 1 or > 1_000_000 || MaximumEnumLabels is < 1 or > 2_000_000 ||
            MaximumDomainConstraints is < 1 or > 1_000_000 || MaximumMetadataBytes is < 1 or > 1024 * 1024 * 1024)
        { throw new ArgumentOutOfRangeException(nameof(MaximumEntries)); }
    }

    internal SchemaModelBudget Budget() => new(Relations with { MaximumMetadataBytes = MaximumMetadataBytes });
}

/// <summary>An enum's labels retain PostgreSQL ordering; a domain retains its base type and constraints.</summary>
public sealed class SchemaTypeContract
{
    public SchemaTypeContract(SchemaRelationIdentity identity, string kind, IEnumerable<string>? enumLabels = null,
        string? baseType = null, bool isNullable = true, string? defaultSql = null, string? collation = null,
        IEnumerable<SchemaConstraint>? constraints = null, SchemaCatalogLimits? limits = null)
    {
        limits ??= SchemaCatalogLimits.Default;
        limits.Validate();
        var budget = limits.Budget();
        CatalogValidation.Identity(budget, identity);
        if (kind is not ("e" or "d")) { throw new ArgumentException("Only enum and domain type contracts are supported.", nameof(kind)); }
        budget.Text(baseType); budget.Text(defaultSql); budget.Text(collation);
        var labels = SchemaModelBudget.Materialize(enumLabels ?? [], limits.MaximumEnumLabels,
            value => budget.Text(value, identifier: true));
        var checks = SchemaModelBudget.Materialize(constraints ?? [], limits.MaximumDomainConstraints, value =>
        {
            budget.Add(32); budget.Text(value.Name, required: true, identifier: true);
            budget.Text(value.Definition, required: true);
            if (value.Kind is not ("c" or "n")) { throw new ArgumentException("Domain contracts require check or not-null constraints."); }
        });
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Count ||
            checks.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != checks.Count ||
            kind == "e" && (baseType is not null || !isNullable || defaultSql is not null || collation is not null || checks.Count != 0) ||
            kind == "d" && (string.IsNullOrEmpty(baseType) || labels.Count != 0))
        { throw new ArgumentException("The type contract is inconsistent."); }
        Identity = identity; Kind = kind; BaseType = baseType; IsNullable = isNullable;
        DefaultSql = defaultSql; Collation = collation;
        EnumLabels = Array.AsReadOnly(labels.ToArray());
        Constraints = Array.AsReadOnly(checks.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        MetadataBytes = budget.UsedBytes + 96;
    }

    public SchemaRelationIdentity Identity { get; }
    public string Kind { get; }
    public ReadOnlyCollection<string> EnumLabels { get; }
    public string? BaseType { get; }
    public bool IsNullable { get; }
    public string? DefaultSql { get; }
    public string? Collation { get; }
    public ReadOnlyCollection<SchemaConstraint> Constraints { get; }
    internal long MetadataBytes { get; }
}

public sealed record SchemaRoutineContract(SchemaRelationIdentity Identity, string IdentityArguments, string Kind,
    string ResultType, string Language, bool SecurityDefiner, bool IsStrict, string Volatility, string ParallelSafety,
    string DefinitionSql);

public sealed record SchemaPrivilegeContract(string ObjectKind, SchemaRelationIdentity Identity, string? Member,
    string? IdentityArguments, string Grantor, string Grantee, string Privilege, bool IsGrantable, bool IsPublic = false);

public sealed record SchemaPublicationMemberContract(string Publication, SchemaRelationIdentity Relation,
    bool AllTables, bool ViaPartitionRoot, bool PublishesInsert, bool PublishesUpdate, bool PublishesDelete,
    bool PublishesTruncate, string Columns, string? RowFilterSql);

public sealed record SchemaExtensionContract(string Name, string Schema, string Version, bool IsRelocatable);

/// <summary>A distinct versioned contract adds types, routines, object ACLs and publication membership without changing relation format 1.</summary>
public sealed class SchemaCatalogSnapshot
{
    public const int CurrentFormatVersion = 1;

    public SchemaCatalogSnapshot(SchemaSnapshot relations, IEnumerable<SchemaTypeContract>? types = null,
        IEnumerable<SchemaRoutineContract>? routines = null, IEnumerable<SchemaPrivilegeContract>? privileges = null,
        IEnumerable<SchemaPublicationMemberContract>? publications = null, IEnumerable<SchemaExtensionContract>? extensions = null,
        SchemaCatalogLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(relations);
        limits ??= SchemaCatalogLimits.Default;
        limits.Validate();
        // Revalidate independently constructed relation models against this envelope's admission contract.
        Relations = new SchemaSnapshot(relations.Relations, limits.Relations);
        var budget = limits.Budget();
        foreach (var relation in Relations.Relations) { budget.Add(relation.MetadataBytes); }
        var count = 0;
        long labels = 0, checks = 0;
        List<T> Read<T>(IEnumerable<T> source, Action<T> validate) where T : class =>
            SchemaModelBudget.Materialize(source, limits.MaximumEntries, value =>
            {
                if (++count > limits.MaximumEntries) { throw new SchemaCaptureLimitException(); }
                budget.Add(96); validate(value);
            });
        var typeList = Read(types ?? [], value =>
        {
            budget.Add(value.MetadataBytes);
            labels += value.EnumLabels.Count; checks += value.Constraints.Count;
            if (labels > limits.MaximumEnumLabels || checks > limits.MaximumDomainConstraints) { throw new SchemaCaptureLimitException(); }
            _ = new SchemaTypeContract(value.Identity, value.Kind, value.EnumLabels, value.BaseType, value.IsNullable,
                value.DefaultSql, value.Collation, value.Constraints, limits);
        });
        var routineList = Read(routines ?? [], value =>
        {
            CatalogValidation.Identity(budget, value.Identity);
            budget.Text(value.IdentityArguments);
            budget.Text(value.ResultType); budget.Text(value.Language, required: true, identifier: true);
            budget.Text(value.DefinitionSql, required: true);
            if (value.IdentityArguments is null || value.ResultType is null || value.Kind is not ("f" or "p" or "w") ||
                value.Volatility is not ("i" or "s" or "v") || value.ParallelSafety is not ("s" or "r" or "u"))
            { throw new ArgumentException("Invalid routine contract."); }
        });
        var privilegeList = Read(privileges ?? [], value =>
        {
            CatalogValidation.Identity(budget, value.Identity);
            budget.Text(value.Member, identifier: true); budget.Text(value.IdentityArguments);
            budget.Text(value.Grantor, required: true, identifier: true); budget.Text(value.Grantee, required: true, identifier: true);
            budget.Text(value.Privilege, required: true);
            if (value.ObjectKind is not ("schema" or "relation" or "column" or "routine" or "type" or "publication") ||
                value.ObjectKind == "column" && string.IsNullOrEmpty(value.Member) ||
                value.ObjectKind != "column" && value.Member is not null ||
                value.ObjectKind == "routine" && value.IdentityArguments is null ||
                value.ObjectKind != "routine" && value.IdentityArguments is not null || value.IsPublic && value.Grantee != "PUBLIC" ||
                value.Privilege == "OWNER" && (value.IsPublic || value.Grantor != value.Grantee || value.Member is not null || !value.IsGrantable))
            { throw new ArgumentException("Invalid privilege contract."); }
        });
        var publicationList = Read(publications ?? [], value =>
        {
            CatalogValidation.Identity(budget, value.Relation);
            budget.Text(value.Publication, required: true, identifier: true); budget.Text(value.Columns);
            budget.Text(value.RowFilterSql);
            if (value.Columns is null) { throw new ArgumentException("Publication columns must be present."); }
        });
        var extensionList = Read(extensions ?? [], value =>
        {
            budget.Text(value.Name, required: true, identifier: true); budget.Text(value.Schema, required: true, identifier: true);
            budget.Text(value.Version, required: true);
        });
        Types = Ordered(typeList, value => CatalogValidation.Key(value.Identity));
        Routines = Ordered(routineList, value => CatalogValidation.Key(value.Identity) + "\0" + value.IdentityArguments);
        Privileges = Ordered(privilegeList, CatalogValidation.PrivilegeKey);
        Publications = Ordered(publicationList, value => value.Publication + "\0" + CatalogValidation.Key(value.Relation));
        Extensions = Ordered(extensionList, value => value.Name);
        Fingerprint = CalculateFingerprint();
    }

    public SchemaSnapshot Relations { get; }
    public ReadOnlyCollection<SchemaTypeContract> Types { get; }
    public ReadOnlyCollection<SchemaRoutineContract> Routines { get; }
    public ReadOnlyCollection<SchemaPrivilegeContract> Privileges { get; }
    public ReadOnlyCollection<SchemaPublicationMemberContract> Publications { get; }
    public ReadOnlyCollection<SchemaExtensionContract> Extensions { get; }
    public string Fingerprint { get; }

    private static ReadOnlyCollection<T> Ordered<T>(List<T> values, Func<T, string> key)
    {
        var sorted = values.OrderBy(key, StringComparer.Ordinal).ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (key(sorted[index - 1]) == key(sorted[index])) { throw new ArgumentException("Catalogue contract identities must be unique."); }
        }
        return Array.AsReadOnly(sorted);
    }

    private string CalculateFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Number(int value)
        {
            Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); hash.AppendData(bytes);
        }
        void Text(string? value)
        {
            if (value is null) { Number(-1); return; }
            var length = Encoding.UTF8.GetByteCount(value); Number(length);
            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
            try { var written = Encoding.UTF8.GetBytes(value, rented); hash.AppendData(rented.AsSpan(0, written)); }
            finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
        }
        void Identity(SchemaRelationIdentity value) { Text(value.Schema); Text(value.Name); }
        Text("BlueTusk.Schema.Catalog:1"); Text(Relations.Fingerprint); Number(Types.Count);
        foreach (var value in Types)
        {
            Identity(value.Identity); Text(value.Kind); Text(value.BaseType); Number(value.IsNullable ? 1 : 0);
            Text(value.DefaultSql); Text(value.Collation); Number(value.EnumLabels.Count);
            foreach (var label in value.EnumLabels) { Text(label); }
            Number(value.Constraints.Count);
            foreach (var check in value.Constraints) { Text(check.Name); Text(check.Kind); Text(check.Definition); }
        }
        Number(Routines.Count);
        foreach (var value in Routines)
        {
            Identity(value.Identity); Text(value.IdentityArguments); Text(value.Kind); Text(value.ResultType); Text(value.Language);
            Number(value.SecurityDefiner ? 1 : 0); Number(value.IsStrict ? 1 : 0); Text(value.Volatility);
            Text(value.ParallelSafety); Text(value.DefinitionSql);
        }
        Number(Privileges.Count);
        foreach (var value in Privileges)
        {
            Text(value.ObjectKind); Identity(value.Identity); Text(value.Member); Text(value.IdentityArguments);
            Text(value.Grantor); Text(value.Grantee); Text(value.Privilege); Number(value.IsGrantable ? 1 : 0); Number(value.IsPublic ? 1 : 0);
        }
        Number(Publications.Count);
        foreach (var value in Publications)
        {
            Text(value.Publication); Identity(value.Relation); Number(value.AllTables ? 1 : 0); Number(value.ViaPartitionRoot ? 1 : 0);
            Number(value.PublishesInsert ? 1 : 0); Number(value.PublishesUpdate ? 1 : 0); Number(value.PublishesDelete ? 1 : 0);
            Number(value.PublishesTruncate ? 1 : 0); Text(value.Columns); Text(value.RowFilterSql);
        }
        Number(Extensions.Count);
        foreach (var value in Extensions) { Text(value.Name); Text(value.Schema); Text(value.Version); Number(value.IsRelocatable ? 1 : 0); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal static class CatalogValidation
{
    internal static void Identity(SchemaModelBudget budget, SchemaRelationIdentity identity)
    { budget.Text(identity.Schema, required: true, identifier: true); budget.Text(identity.Name, required: true, identifier: true); }
    internal static string Key(SchemaRelationIdentity value) => value.Schema + "\0" + value.Name;
    internal static string PrivilegeKey(SchemaPrivilegeContract value) => value.ObjectKind + "\0" + Key(value.Identity) + "\0" +
        value.Member + "\0" + value.IdentityArguments + "\0" + value.Grantor + "\0" + value.Grantee + "\0" + value.IsPublic + "\0" + value.Privilege;
}
