using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueTusk.Edge.Http;

public sealed record EdgeHttpSnapshotPage(IReadOnlyList<EdgeRecord> Records, string? NextAfterId);

/// <summary>All Int64 values are decimal strings, UUIDs are stable strings, and payload bytes are base64 without JSON normalization.</summary>
public static class EdgeWireCodec
{
    public static byte[] SerializeOrderedReceiptHorizon(Guid throughMutationId, int maxReceipts) =>
        JsonSerializer.SerializeToUtf8Bytes(new WireOrderedReceiptHorizon(throughMutationId.ToString("D"), maxReceipts), EdgeWireJsonContext.Default.WireOrderedReceiptHorizon);

    public static (Guid ThroughMutationId, int MaxReceipts) DeserializeOrderedReceiptHorizon(ReadOnlySpan<byte> bytes)
    {
        var value = JsonSerializer.Deserialize(bytes, EdgeWireJsonContext.Default.WireOrderedReceiptHorizon) ?? throw new JsonException("Ordered receipt horizon is missing.");
        if (value.MaxReceipts is < 1 or > 10_000) { throw new JsonException("Ordered receipt horizon exceeds the bounded row limit."); }
        return (Guid.ParseExact(value.ThroughId, "D"), value.MaxReceipts);
    }
    public static byte[] SerializeMutation(EdgeMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return JsonSerializer.SerializeToUtf8Bytes(new WireMutation(mutation.Id.ToString("D"), mutation.DocumentId, Number(mutation.ExpectedRevision),
            mutation.Kind is EdgeMutationKind.Upsert ? "upsert" : "delete", Convert.ToBase64String(mutation.Payload.Span)), EdgeWireJsonContext.Default.WireMutation);
    }
    public static EdgeMutation DeserializeMutation(EdgeScope scope, ReadOnlySpan<byte> bytes, int maxPayloadBytes)
    {
        var value = JsonSerializer.Deserialize(bytes, EdgeWireJsonContext.Default.WireMutation) ?? throw new JsonException("Mutation is missing.");
        return new(scope, Guid.ParseExact(value.Id, "D"), value.DocumentId, ParseNumber(value.ExpectedRevision),
            value.Kind switch { "upsert" => EdgeMutationKind.Upsert, "delete" => EdgeMutationKind.Delete, _ => throw new JsonException("Unsupported mutation kind.") }, Payload(value.Payload, maxPayloadBytes));
    }
    public static byte[] SerializeOutcome(EdgeMutationOutcome outcome) => JsonSerializer.SerializeToUtf8Bytes(
        new WireOutcome(outcome.Kind is EdgeMutationOutcomeKind.Applied ? "applied" : "conflict", outcome.ServerRecord is null ? null : Record(outcome.ServerRecord)), EdgeWireJsonContext.Default.WireOutcome);
    public static EdgeMutationOutcome DeserializeOutcome(ReadOnlySpan<byte> bytes, int maxPayloadBytes)
    {
        var value = JsonSerializer.Deserialize(bytes, EdgeWireJsonContext.Default.WireOutcome) ?? throw new JsonException("Outcome is missing.");
        return new(value.Kind switch { "applied" => EdgeMutationOutcomeKind.Applied, "conflict" => EdgeMutationOutcomeKind.Conflict, _ => throw new JsonException("Unsupported outcome kind.") },
            value.Record is null ? null : Record(value.Record, maxPayloadBytes));
    }
    public static byte[] SerializeSnapshot(EdgeSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(new WireSnapshot(snapshot.Id.ToString("D"), Number(snapshot.Position)), EdgeWireJsonContext.Default.WireSnapshot);
    public static EdgeSnapshot DeserializeSnapshot(ReadOnlySpan<byte> bytes)
    {
        var value = JsonSerializer.Deserialize(bytes, EdgeWireJsonContext.Default.WireSnapshot) ?? throw new JsonException("Snapshot is missing.");
        return new(Guid.ParseExact(value.Id, "D"), ParseNumber(value.Position));
    }
    public static byte[] SerializeSnapshotPage(IReadOnlyList<EdgeRecord> records, string? nextAfterId) =>
        JsonSerializer.SerializeToUtf8Bytes(new WireSnapshotPage(records.Select(Record).ToArray(), nextAfterId), EdgeWireJsonContext.Default.WireSnapshotPage);
    public static EdgeHttpSnapshotPage DeserializeSnapshotPage(ReadOnlySpan<byte> bytes, int maxRecords, int maxPayloadBytes)
    {
        var value = JsonSerializer.Deserialize(bytes, EdgeWireJsonContext.Default.WireSnapshotPage) ?? throw new JsonException("Snapshot page is missing.");
        if (value.Records is null || value.Records.Length > maxRecords || value.Records.Any(static record => record is null)) { throw new JsonException("Snapshot page is missing records or exceeds its row limit."); }
        return new(Array.AsReadOnly(value.Records.Select(record => Record(record, maxPayloadBytes)).ToArray()), value.NextAfterId);
    }
    public static byte[] SerializeChanges(EdgeChangeBatch batch) => JsonSerializer.SerializeToUtf8Bytes(
        new WireChanges(Number(batch.FromPosition), Number(batch.ToPosition), batch.Records.Select(Record).ToArray()), EdgeWireJsonContext.Default.WireChanges);
    public static EdgeChangeBatch DeserializeChanges(ReadOnlySpan<byte> bytes, int maxRecords, int maxPayloadBytes)
    {
        var value = JsonSerializer.Deserialize(bytes, EdgeWireJsonContext.Default.WireChanges) ?? throw new JsonException("Change batch is missing.");
        if (value.Records is null || value.Records.Length > maxRecords || value.Records.Any(static record => record is null)) { throw new JsonException("Change batch is missing records or exceeds its row limit."); }
        return new(ParseNumber(value.FromPosition), ParseNumber(value.ToPosition), Array.AsReadOnly(value.Records.Select(record => Record(record, maxPayloadBytes)).ToArray()));
    }
    public static string Number(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value.ToString(CultureInfo.InvariantCulture);
    }
    public static long ParseNumber(string value)
    {
        if (value is null || value.Length is 0 or > 19 || value.Length > 1 && value[0] == '0' || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
        { throw new JsonException("A canonical nonnegative Int64 decimal string is required."); }
        return number;
    }
    private static WireRecord Record(EdgeRecord record) => new(record.Id, Number(record.Revision), Convert.ToBase64String(record.Payload.Span), record.Deleted);
    private static EdgeRecord Record(WireRecord record, int maxPayloadBytes) => new(record.Id, ParseNumber(record.Revision), Payload(record.Payload, maxPayloadBytes), record.Deleted);
    private static byte[] Payload(string value, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (value is null || value.Length > (maximum + 2L) / 3 * 4) { throw new JsonException("Payload exceeds its byte budget."); }
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length > maximum) { throw new JsonException("Payload exceeds its byte budget."); }
        return bytes;
    }
}

internal sealed record WireMutation(string Id, string DocumentId, string ExpectedRevision, string Kind, string Payload);
internal sealed record WireRecord(string Id, string Revision, string Payload, bool Deleted);
internal sealed record WireOutcome(string Kind, WireRecord? Record);
internal sealed record WireSnapshot(string Id, string Position);
internal sealed record WireSnapshotPage(WireRecord[] Records, string? NextAfterId);
internal sealed record WireChanges(string FromPosition, string ToPosition, WireRecord[] Records);
internal sealed record WireOrderedReceiptHorizon(string ThroughId, int MaxReceipts);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WireMutation))]
[JsonSerializable(typeof(WireOutcome))]
[JsonSerializable(typeof(WireSnapshot))]
[JsonSerializable(typeof(WireSnapshotPage))]
[JsonSerializable(typeof(WireChanges))]
[JsonSerializable(typeof(WireOrderedReceiptHorizon))]
internal sealed partial class EdgeWireJsonContext : JsonSerializerContext;
