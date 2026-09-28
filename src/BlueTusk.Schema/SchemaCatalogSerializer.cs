using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueTusk.Schema;

public static class SchemaCatalogSerializer
{
    public static byte[] Serialize(SchemaCatalogSnapshot snapshot, int maximumDocumentBytes = SchemaSnapshotSerializer.MaximumDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDocumentBytes, SchemaSnapshotSerializer.MaximumDocumentBytes);
        var relations = new SchemaSnapshotEnvelope(SchemaSnapshot.CurrentFormatVersion, snapshot.Relations.Fingerprint,
            snapshot.Relations.Relations.Select(value => new SchemaRelationDocument(value.Identity.Schema, value.Identity.Name,
                value.Kind, value.RowSecurity, value.ForceRowSecurity, value.ReplicaIdentity, value.Columns.ToArray(),
                value.Constraints.ToArray(), value.Indexes.ToArray(), value.Policies.ToArray(), value.DefinitionSql)).ToArray());
        var envelope = new SchemaCatalogEnvelope(SchemaCatalogSnapshot.CurrentFormatVersion, snapshot.Fingerprint, relations,
            snapshot.Types.Select(value => new SchemaTypeDocument(value.Identity, value.Kind, value.EnumLabels.ToArray(),
                value.BaseType, value.IsNullable, value.DefaultSql, value.Collation, value.Constraints.ToArray())).ToArray(),
            snapshot.Routines.ToArray(), snapshot.Privileges.ToArray(), snapshot.Publications.ToArray(), snapshot.Extensions.ToArray());
        using var buffer = new SchemaSnapshotSerializer.BoundedJsonBuffer(maximumDocumentBytes);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, envelope, SchemaCatalogJson.Default.SchemaCatalogEnvelope);
        writer.Flush(); return buffer.ToArray();
    }

    public static SchemaCatalogSnapshot Deserialize(ReadOnlySpan<byte> utf8Json, SchemaCatalogLimits? limits = null)
    {
        if (utf8Json.Length > SchemaSnapshotSerializer.MaximumDocumentBytes) { throw new SchemaCaptureLimitException(); }
        limits ??= SchemaCatalogLimits.Default; limits.Validate();
        Preflight(utf8Json, limits);
        var value = JsonSerializer.Deserialize(utf8Json, SchemaCatalogJson.Default.SchemaCatalogEnvelope)
            ?? throw new JsonException("A catalogue envelope is required.");
        if (value.FormatVersion != SchemaCatalogSnapshot.CurrentFormatVersion || value.Relations is null ||
            value.Relations.FormatVersion != SchemaSnapshot.CurrentFormatVersion || value.Relations.Relations is null ||
            value.Types is null || value.Routines is null || value.Privileges is null || value.Publications is null || value.Extensions is null)
        { throw new JsonException("The catalogue envelope format is invalid or unsupported."); }
        try
        {
            var relationSnapshot = new SchemaSnapshot(value.Relations.Relations.Select(relation => relation is null ||
                relation.Columns is null || relation.Constraints is null || relation.Indexes is null || relation.Policies is null
                ? throw new ArgumentException("Invalid relation contract.") : new SchemaRelation(new(relation.Schema, relation.Name),
                    relation.Kind, relation.RowSecurity, relation.ForceRowSecurity, relation.ReplicaIdentity, relation.Columns,
                    relation.Constraints, relation.Indexes, relation.Policies, relation.DefinitionSql, limits.Relations)), limits.Relations);
            var snapshot = new SchemaCatalogSnapshot(relationSnapshot,
                value.Types.Select(type => type is null || type.EnumLabels is null || type.Constraints is null
                    ? throw new ArgumentException("Invalid type contract.") : new SchemaTypeContract(type.Identity, type.Kind,
                        type.EnumLabels, type.BaseType, type.IsNullable, type.DefaultSql, type.Collation, type.Constraints, limits)),
                value.Routines, value.Privileges, value.Publications, value.Extensions, limits);
            if (snapshot.Relations.Fingerprint != value.Relations.Fingerprint || snapshot.Fingerprint != value.Fingerprint)
            { throw new JsonException("The catalogue fingerprint does not match its contents."); }
            return snapshot;
        }
        catch (ArgumentException error) { throw new JsonException("The catalogue contract is invalid.", error); }
    }

    private static void Preflight(ReadOnlySpan<byte> json, SchemaCatalogLimits limits)
    {
        var reader = new Utf8JsonReader(json, new() { MaxDepth = 32 });
        Span<int> arrays = stackalloc int[33]; arrays.Clear();
        Span<int> counts = stackalloc int[13]; counts.Clear();
        var nextArray = 0; var entries = 0; var relationDepth = -1; var nextRelation = false;
        long bytes = 0, relationBytes = 0, tokens = 0;
        var maximumTokens = 128L + limits.MaximumEntries * 40L + limits.MaximumEnumLabels * 2L +
            limits.MaximumDomainConstraints * 10L + limits.Relations.MaximumRelations * 32L +
            limits.Relations.MaximumColumns * 24L + limits.Relations.MaximumConstraints * 10L +
            limits.Relations.MaximumIndexes * 10L + limits.Relations.MaximumPolicies * 18L;
        while (reader.Read())
        {
            if (++tokens > maximumTokens) { throw new SchemaCaptureLimitException(); }
            if (reader.TokenType == JsonTokenType.StartObject && nextRelation) { relationDepth = reader.CurrentDepth; }
            if (reader.TokenType != JsonTokenType.PropertyName) { nextRelation = false; }
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == relationDepth) { relationDepth = -1; }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                nextRelation = reader.CurrentDepth == 1 && reader.ValueTextEquals("Relations"u8);
                var insideTypes = reader.ValueTextEquals("Constraints"u8) && arrays[..reader.CurrentDepth].Contains(1);
                nextArray = reader.ValueTextEquals("Types"u8) ? 1 : reader.ValueTextEquals("Routines"u8) ? 2 :
                    reader.ValueTextEquals("Privileges"u8) ? 3 : reader.ValueTextEquals("Publications"u8) ? 4 :
                    reader.ValueTextEquals("Extensions"u8) ? 5 : reader.ValueTextEquals("EnumLabels"u8) ? 6 :
                    reader.ValueTextEquals("Relations"u8) ? 7 : reader.ValueTextEquals("Columns"u8) ? 8 :
                    reader.ValueTextEquals("Constraints"u8) ? insideTypes ? 9 : 10 :
                    reader.ValueTextEquals("Indexes"u8) ? 11 : reader.ValueTextEquals("Policies"u8) ? 12 : 0;
            }
            else if (reader.TokenType == JsonTokenType.StartArray) { arrays[reader.CurrentDepth] = nextArray; nextArray = 0; }
            else if (reader.TokenType == JsonTokenType.EndArray) { arrays[reader.CurrentDepth] = 0; }
            else
            {
                nextArray = 0;
                if (reader.CurrentDepth > 0 && arrays[reader.CurrentDepth - 1] is var kind && kind != 0 &&
                    reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != (kind == 6 ? JsonTokenType.String : JsonTokenType.StartObject))
                    { throw new JsonException("Catalogue arrays require non-null members of the declared kind."); }
                    var maximum = kind switch
                    {
                        <= 5 => limits.MaximumEntries,
                        6 => limits.MaximumEnumLabels,
                        7 => limits.Relations.MaximumRelations,
                        8 => limits.Relations.MaximumColumns,
                        9 => limits.MaximumDomainConstraints,
                        10 => limits.Relations.MaximumConstraints,
                        11 => limits.Relations.MaximumIndexes,
                        _ => limits.Relations.MaximumPolicies,
                    };
                    if (++counts[kind] > maximum || kind <= 5 && ++entries > limits.MaximumEntries) { throw new SchemaCaptureLimitException(); }
                    var charge = kind switch { 1 => 192, <= 5 => 96, 6 => 0, 7 => 128, 8 or 12 => 64, _ => 32 };
                    bytes += charge;
                    if (relationDepth >= 0) { relationBytes += charge; }
                }
                if (reader.TokenType == JsonTokenType.String)
                {
                    var remaining = Math.Min(limits.Relations.MaximumStringBytes, limits.MaximumMetadataBytes - bytes);
                    if (relationDepth >= 0) { remaining = Math.Min(remaining, limits.Relations.MaximumMetadataBytes - relationBytes); }
                    var length = SchemaJsonAdmission.DecodedByteLength(ref reader, remaining);
                    bytes += length;
                    if (relationDepth >= 0) { relationBytes += length; }
                }
                if (bytes > limits.MaximumMetadataBytes || relationBytes > limits.Relations.MaximumMetadataBytes) { throw new SchemaCaptureLimitException(); }
            }
        }
    }
}

internal sealed record SchemaCatalogEnvelope(int FormatVersion, string Fingerprint, SchemaSnapshotEnvelope Relations,
    SchemaTypeDocument[] Types, SchemaRoutineContract[] Routines, SchemaPrivilegeContract[] Privileges,
    SchemaPublicationMemberContract[] Publications, SchemaExtensionContract[] Extensions);
internal sealed record SchemaTypeDocument(SchemaRelationIdentity Identity, string Kind, string[] EnumLabels, string? BaseType,
    bool IsNullable, string? DefaultSql, string? Collation, SchemaConstraint[] Constraints);

[JsonSourceGenerationOptions(MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SchemaCatalogEnvelope))]
internal sealed partial class SchemaCatalogJson : JsonSerializerContext;
