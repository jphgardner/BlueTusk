using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueTusk.Schema;

public static class SchemaSnapshotSerializer
{
    public const int MaximumDocumentBytes = 64 * 1024 * 1024;

    public static byte[] Serialize(SchemaSnapshot snapshot, int maximumDocumentBytes = MaximumDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDocumentBytes, MaximumDocumentBytes);
        var document = new SchemaSnapshotEnvelope(SchemaSnapshot.CurrentFormatVersion, snapshot.Fingerprint,
            snapshot.Relations.Select(relation => new SchemaRelationDocument(relation.Identity.Schema, relation.Identity.Name,
                relation.Kind, relation.RowSecurity, relation.ForceRowSecurity, relation.ReplicaIdentity,
                relation.Columns.ToArray(), relation.Constraints.ToArray(), relation.Indexes.ToArray(),
                relation.Policies.ToArray(), relation.DefinitionSql)).ToArray());
        using var buffer = new BoundedJsonBuffer(maximumDocumentBytes);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, document, SchemaJsonContext.Default.SchemaSnapshotEnvelope);
        writer.Flush();
        return buffer.ToArray();
    }

    public static SchemaSnapshot Deserialize(ReadOnlySpan<byte> utf8Json, SchemaSnapshotLimits? limits = null)
    {
        if (utf8Json.Length > MaximumDocumentBytes)
        {
            throw new SchemaCaptureLimitException();
        }

        limits ??= SchemaSnapshotLimits.Default;
        limits.Validate();
        Preflight(utf8Json, limits);
        var document = JsonSerializer.Deserialize(utf8Json, SchemaJsonContext.Default.SchemaSnapshotEnvelope)
            ?? throw new JsonException("A schema snapshot document is required.");
        if (document.FormatVersion != SchemaSnapshot.CurrentFormatVersion)
        {
            throw new JsonException("The schema snapshot format version is unsupported.");
        }

        if (document.Relations is null ||
            document.Relations.Any(relation => relation is null || relation.Columns is null ||
                relation.Constraints is null || relation.Indexes is null || relation.Policies is null))
        {
            throw new JsonException("The schema snapshot relation contract is invalid.");
        }

        SchemaSnapshot snapshot;
        try
        {
            snapshot = new SchemaSnapshot(document.Relations.Select(relation => new SchemaRelation(
                new(relation.Schema, relation.Name), relation.Kind, relation.RowSecurity, relation.ForceRowSecurity,
                relation.ReplicaIdentity, relation.Columns, relation.Constraints, relation.Indexes, relation.Policies,
                relation.DefinitionSql, limits)), limits);
        }
        catch (ArgumentException error) { throw new JsonException("The schema snapshot metadata contract is invalid.", error); }
        if (!string.Equals(snapshot.Fingerprint, document.Fingerprint, StringComparison.Ordinal))
        {
            throw new JsonException("The schema snapshot fingerprint does not match its contents.");
        }

        return snapshot;
    }

    private static void Preflight(ReadOnlySpan<byte> json, SchemaSnapshotLimits limits)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
        Span<int> arrays = stackalloc int[33];
        Span<int> counts = stackalloc int[6];
        arrays.Clear();
        counts.Clear();
        var nextArray = 0;
        long tokens = 0, metadataBytes = 0;
        var maximumTokens = 64L + limits.MaximumRelations * 32L + limits.MaximumColumns * 24L +
            limits.MaximumConstraints * 10L + limits.MaximumIndexes * 10L + limits.MaximumPolicies * 18L;
        while (reader.Read())
        {
            if (++tokens > maximumTokens) { throw new SchemaCaptureLimitException(); }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                nextArray = reader.ValueTextEquals("Relations"u8) ? 1 : reader.ValueTextEquals("Columns"u8) ? 2 :
                    reader.ValueTextEquals("Constraints"u8) ? 3 : reader.ValueTextEquals("Indexes"u8) ? 4 :
                    reader.ValueTextEquals("Policies"u8) ? 5 : 0;
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                arrays[reader.CurrentDepth] = nextArray;
                nextArray = 0;
            }
            else if (reader.TokenType == JsonTokenType.EndArray) { arrays[reader.CurrentDepth] = 0; }
            else
            {
                nextArray = 0;
                if (reader.CurrentDepth > 0 && arrays[reader.CurrentDepth - 1] is var kind && kind != 0 &&
                    reader.TokenType is not JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) { throw new JsonException("Schema arrays require non-null objects."); }
                    var maximum = kind switch
                    {
                        1 => limits.MaximumRelations,
                        2 => limits.MaximumColumns,
                        3 => limits.MaximumConstraints,
                        4 => limits.MaximumIndexes,
                        _ => limits.MaximumPolicies,
                    };
                    if (++counts[kind] > maximum) { throw new SchemaCaptureLimitException(); }
                    metadataBytes += kind is 1 ? 128 : kind is 2 or 5 ? 64 : 32;
                }
                if (reader.TokenType == JsonTokenType.String)
                {
                    // Bound decoded UTF-8 bytes; the document cap separately bounds
                    // escaped input. Serialization must roundtrip every admitted value.
                    var length = SchemaJsonAdmission.DecodedByteLength(ref reader,
                        Math.Min(limits.MaximumStringBytes, limits.MaximumMetadataBytes - metadataBytes));
                    metadataBytes += length;
                }
                if (metadataBytes > limits.MaximumMetadataBytes) { throw new SchemaCaptureLimitException(); }
            }
        }
    }

    internal sealed class BoundedJsonBuffer(int maximum) : IBufferWriter<byte>, IDisposable
    {
        private const int WriterSlack = 4096;
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, maximum + WriterSlack));
        private int _written;

        public void Advance(int count)
        {
            if (count < 0 || count > Math.Min(_buffer.Length, maximum) - _written) { throw new SchemaCaptureLimitException(); }
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(_written); }
        public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();
        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);

        private void Ensure(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            sizeHint = Math.Max(sizeHint, 1);
            // Utf8JsonWriter asks for growth hints larger than its eventual writes.
            // Keep one bounded slack block while Advance enforces the exact output cap.
            if (sizeHint > maximum - _written + WriterSlack) { throw new SchemaCaptureLimitException(); }
            if (sizeHint <= _buffer.Length - _written) { return; }
            var replacement = ArrayPool<byte>.Shared.Rent(Math.Min(maximum + WriterSlack, Math.Max(_written + sizeHint, _buffer.Length * 2)));
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = replacement;
        }
    }
}

internal sealed record SchemaSnapshotEnvelope(int FormatVersion, string Fingerprint, SchemaRelationDocument[] Relations);

internal sealed record SchemaRelationDocument(string Schema, string Name, string Kind, bool RowSecurity, bool ForceRowSecurity,
    string ReplicaIdentity, SchemaColumn[] Columns, SchemaConstraint[] Constraints, SchemaIndex[] Indexes,
    SchemaPolicy[] Policies, string? DefinitionSql);

[JsonSourceGenerationOptions(MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SchemaSnapshotEnvelope))]
internal sealed partial class SchemaJsonContext : JsonSerializerContext;
