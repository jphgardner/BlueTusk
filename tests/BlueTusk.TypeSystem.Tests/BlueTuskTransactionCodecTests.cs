using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTransactionCodecTests
{
    [Fact]
    public void Transaction_and_command_identifiers_cover_unsigned_wire_ranges()
    {
        var transactionId = new BlueTuskTransactionId(uint.MaxValue);
        var commandId = new BlueTuskCommandId(uint.MaxValue);
        var fullTransactionId = new BlueTuskFullTransactionId(ulong.MaxValue);

        Assert.Equal(
            transactionId,
            RoundTrip(
                new BlueTuskTransactionIdCodec(),
                BlueTuskBuiltInTypes.Xid,
                transactionId,
                BlueTuskDataFormat.Binary));
        Assert.Equal(
            commandId,
            RoundTrip(
                new BlueTuskCommandIdCodec(),
                BlueTuskBuiltInTypes.Cid,
                commandId,
                BlueTuskDataFormat.Text));
        Assert.Equal(
            fullTransactionId,
            RoundTrip(
                new BlueTuskFullTransactionIdCodec(),
                BlueTuskBuiltInTypes.Xid8,
                fullTransactionId,
                BlueTuskDataFormat.Binary));

        Assert.Equal(
            "FFFFFFFF",
            Convert.ToHexString(
                Write(
                    new BlueTuskTransactionIdCodec(),
                    BlueTuskBuiltInTypes.Xid,
                    transactionId,
                    BlueTuskDataFormat.Binary)));
        Assert.Equal(
            "FFFFFFFFFFFFFFFF",
            Convert.ToHexString(
                Write(
                    new BlueTuskFullTransactionIdCodec(),
                    BlueTuskBuiltInTypes.Xid8,
                    fullTransactionId,
                    BlueTuskDataFormat.Binary)));
    }

    [Fact]
    public void Snapshot_round_trips_identical_pg_and_legacy_binary_layouts()
    {
        var value = new BlueTuskTransactionSnapshot(10, 20, [12, 15]);
        var codec = new BlueTuskTransactionSnapshotCodec();
        const string expectedBinary =
            "00000002" +
            "000000000000000A" +
            "0000000000000014" +
            "000000000000000C" +
            "000000000000000F";

        Assert.Equal(
            value,
            RoundTrip(codec, BlueTuskBuiltInTypes.PgSnapshot, value, BlueTuskDataFormat.Binary));
        Assert.Equal(
            value,
            RoundTrip(codec, BlueTuskBuiltInTypes.TxidSnapshot, value, BlueTuskDataFormat.Text));
        Assert.Equal(
            expectedBinary,
            Convert.ToHexString(
                Write(
                    codec,
                    BlueTuskBuiltInTypes.PgSnapshot,
                    value,
                    BlueTuskDataFormat.Binary)));
        Assert.Equal(
            "10:20:12,15",
            Encoding.UTF8.GetString(
                Write(
                    codec,
                    BlueTuskBuiltInTypes.PgSnapshot,
                    value,
                    BlueTuskDataFormat.Text)));
    }

    [Fact]
    public void Snapshot_is_immutable_and_validates_postgresql_invariants()
    {
        ulong[] inProgress = [12, 15];
        var value = new BlueTuskTransactionSnapshot(10, 20, inProgress);
        inProgress[0] = 19;

        Assert.Equal<ulong>([12, 15], value.InProgressTransactionIds);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlueTuskTransactionSnapshot(20, 10, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlueTuskTransactionSnapshot(10, 20, [9]));
        Assert.Throws<ArgumentException>(
            () => new BlueTuskTransactionSnapshot(10, 20, [15, 12]));
        Assert.Throws<ArgumentException>(
            () => new BlueTuskTransactionSnapshot(10, 20, [12, 12]));
    }

    [Theory]
    [InlineData("10:20")]
    [InlineData("20:10:")]
    [InlineData("10:20:15,12")]
    [InlineData("10:20:not-a-number")]
    public void Malformed_snapshot_text_is_rejected(string text)
    {
        Assert.Throws<InvalidOperationException>(() => ReadTextSnapshot(text));
    }

    [Fact]
    public void Catalogue_composes_transaction_arrays_and_prefers_pg_snapshot_inference()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            CreateArrayType(1011, "_xid", BlueTuskBuiltInTypes.Xid.Id),
            CreateArrayType(1012, "_cid", BlueTuskBuiltInTypes.Cid.Id),
            CreateArrayType(271, "_xid8", BlueTuskBuiltInTypes.Xid8.Id),
            CreateArrayType(2949, "_txid_snapshot", BlueTuskBuiltInTypes.TxidSnapshot.Id),
            CreateArrayType(5039, "_pg_snapshot", BlueTuskBuiltInTypes.PgSnapshot.Id),
        ]);

        AssertCodecClrType(registry, new BlueTuskTypeId(1011), typeof(BlueTuskTransactionId[]));
        AssertCodecClrType(registry, new BlueTuskTypeId(1012), typeof(BlueTuskCommandId[]));
        AssertCodecClrType(registry, new BlueTuskTypeId(271), typeof(BlueTuskFullTransactionId[]));
        AssertCodecClrType(registry, new BlueTuskTypeId(2949), typeof(BlueTuskTransactionSnapshot[]));
        AssertCodecClrType(registry, new BlueTuskTypeId(5039), typeof(BlueTuskTransactionSnapshot[]));

        Assert.True(registry.TryGetType(
            typeof(BlueTuskTransactionSnapshot),
            out var snapshotType,
            out _));
        Assert.Equal(BlueTuskBuiltInTypes.PgSnapshot.Id, snapshotType!.Id);
        Assert.True(registry.TryGetType(
            typeof(BlueTuskTransactionSnapshot[]),
            out var snapshotArrayType,
            out _));
        Assert.Equal(5039U, snapshotArrayType!.Id.Oid);
    }

    private static BlueTuskCatalogueType CreateArrayType(
        uint oid,
        string name,
        BlueTuskTypeId elementType) => new()
        {
            Id = new BlueTuskTypeId(oid),
            Schema = "pg_catalog",
            Name = name,
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = elementType,
        };

    private static void AssertCodecClrType(
        BlueTuskTypeRegistry registry,
        BlueTuskTypeId id,
        Type expected)
    {
        Assert.True(registry.TryGetCodec(id, out var codec));
        Assert.Equal(expected, codec!.ClrType);
    }

    private static BlueTuskTransactionSnapshot ReadTextSnapshot(string text)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes(text));
        return new BlueTuskTransactionSnapshotCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Text,
            BlueTuskBuiltInTypes.PgSnapshot);
    }

    private static T RoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        var bytes = Write(codec, type, value, format);
        var reader = new BlueTuskReader(bytes);
        return codec.ReadTyped(ref reader, format, type);
    }

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[1024];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination.AsSpan(0, writer.WrittenCount).ToArray();
    }
}
