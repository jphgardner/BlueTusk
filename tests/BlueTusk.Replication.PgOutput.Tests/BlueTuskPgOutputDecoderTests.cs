using System.Buffers.Binary;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Replication.PgOutput.Tests;

public sealed class BlueTuskPgOutputDecoderTests
{
    [Fact]
    public void Decodes_transaction_boundaries_and_origin()
    {
        var decoder = new BlueTuskPgOutputDecoder();

        var begin = Assert.IsType<BlueTuskPgOutputBegin>(
            decoder.Decode(
                new PayloadBuilder('B')
                    .UInt64(10)
                    .Int64(1_000_000)
                    .UInt32(42)
                    .Build()));
        var commit = Assert.IsType<BlueTuskPgOutputCommit>(
            decoder.Decode(
                new PayloadBuilder('C')
                    .Byte(0)
                    .UInt64(11)
                    .UInt64(12)
                    .Int64(2_000_000)
                    .Build()));
        var origin = Assert.IsType<BlueTuskPgOutputOrigin>(
            decoder.Decode(
                new PayloadBuilder('O')
                    .UInt64(9)
                    .CString("upstream")
                    .Build()));

        Assert.Equal(42U, begin.TransactionId);
        Assert.Equal(
            new DateTimeOffset(2000, 1, 1, 0, 0, 1, TimeSpan.Zero),
            begin.CommitTimestamp);
        Assert.Equal(11UL, commit.CommitPosition.Value);
        Assert.Equal(12UL, commit.TransactionEndPosition.Value);
        Assert.Equal("upstream", origin.Name);
    }

    [Fact]
    public void Decodes_relation_and_type_metadata()
    {
        var decoder = new BlueTuskPgOutputDecoder();

        var relation = Assert.IsType<BlueTuskPgOutputRelation>(
            decoder.Decode(
                new PayloadBuilder('R')
                    .UInt32(123)
                    .CString("public")
                    .CString("items")
                    .Byte((byte)'d')
                    .Int16(2)
                    .Byte(1)
                    .CString("id")
                    .UInt32(23)
                    .Int32(-1)
                    .Byte(0)
                    .CString("name")
                    .UInt32(25)
                    .Int32(-1)
                    .Build()));
        var type = Assert.IsType<BlueTuskPgOutputType>(
            decoder.Decode(
                new PayloadBuilder('Y')
                    .UInt32(456)
                    .CString("public")
                    .CString("status")
                    .Build()));

        Assert.Equal(123U, relation.RelationId);
        Assert.Equal("items", relation.Name);
        Assert.Equal('d', relation.ReplicaIdentity);
        Assert.Equal(2, relation.Columns.Count);
        Assert.Equal(
            BlueTuskPgOutputRelationColumnOptions.Key,
            relation.Columns[0].Options);
        Assert.Equal(25U, relation.Columns[1].TypeOid);
        Assert.Equal(456U, type.TypeId);
        Assert.Equal("status", type.Name);
    }

    [Fact]
    public void Decodes_insert_tuple_value_variants()
    {
        var decoder = new BlueTuskPgOutputDecoder();

        var insert = Assert.IsType<BlueTuskPgOutputInsert>(
            decoder.Decode(
                new PayloadBuilder('I')
                    .UInt32(123)
                    .Byte((byte)'N')
                    .Int16(4)
                    .Byte((byte)'n')
                    .Byte((byte)'u')
                    .Byte((byte)'t')
                    .Value("hello")
                    .Byte((byte)'b')
                    .Value([1, 2, 3])
                    .Build()));

        Assert.Equal(4, insert.NewRow.Values.Count);
        Assert.Equal(BlueTuskPgOutputTupleValueKind.Null, insert.NewRow.Values[0].Kind);
        Assert.Equal(
            BlueTuskPgOutputTupleValueKind.UnchangedToast,
            insert.NewRow.Values[1].Kind);
        Assert.Equal(
            "hello",
            Encoding.UTF8.GetString(insert.NewRow.Values[2].Data.Span));
        Assert.Equal([1, 2, 3], insert.NewRow.Values[3].Data.ToArray());
    }

    [Fact]
    public void Decodes_update_delete_and_truncate()
    {
        var decoder = new BlueTuskPgOutputDecoder();

        var update = Assert.IsType<BlueTuskPgOutputUpdate>(
            decoder.Decode(
                new PayloadBuilder('U')
                    .UInt32(10)
                    .Byte((byte)'K')
                    .TextTuple("1")
                    .Byte((byte)'N')
                    .TextTuple("2")
                    .Build()));
        var delete = Assert.IsType<BlueTuskPgOutputDelete>(
            decoder.Decode(
                new PayloadBuilder('D')
                    .UInt32(10)
                    .Byte((byte)'O')
                    .TextTuple("2")
                    .Build()));
        var truncate = Assert.IsType<BlueTuskPgOutputTruncate>(
            decoder.Decode(
                new PayloadBuilder('T')
                    .Int32(2)
                    .Byte(3)
                    .UInt32(10)
                    .UInt32(11)
                    .Build()));

        Assert.Equal(BlueTuskPgOutputOldRowKind.Key, update.OldRowKind);
        Assert.NotNull(update.OldRow);
        Assert.Equal(BlueTuskPgOutputOldRowKind.Full, delete.OldRowKind);
        Assert.Equal(
            BlueTuskPgOutputTruncateOptions.Cascade |
            BlueTuskPgOutputTruncateOptions.RestartIdentity,
            truncate.Options);
        Assert.Equal([10U, 11U], truncate.RelationIds);
    }

    [Fact]
    public void Decodes_logical_messages()
    {
        var decoder = new BlueTuskPgOutputDecoder();
        var message = Assert.IsType<BlueTuskPgOutputLogicalMessage>(
            decoder.Decode(
                new PayloadBuilder('M')
                    .Byte(1)
                    .UInt64(99)
                    .CString("audit")
                    .Value([4, 5, 6])
                    .Build()));

        Assert.True(message.IsTransactional);
        Assert.Equal(99UL, message.Position.Value);
        Assert.Equal("audit", message.Prefix);
        Assert.Equal([4, 5, 6], message.Content.ToArray());
    }

    [Fact]
    public void Tracks_stream_segments_and_transaction_prefixes()
    {
        var decoder = new BlueTuskPgOutputDecoder(
            new BlueTuskPgOutputDecoderOptions
            {
                ProtocolVersion = 2,
                StreamingMode = BlueTuskPgOutputStreamingMode.On,
            });

        var start = Assert.IsType<BlueTuskPgOutputStreamStart>(
            decoder.Decode(
                new PayloadBuilder('S')
                    .UInt32(42)
                    .Byte(1)
                    .Build()));
        var insert = Assert.IsType<BlueTuskPgOutputInsert>(
            decoder.Decode(
                new PayloadBuilder('I')
                    .UInt32(42)
                    .UInt32(10)
                    .Byte((byte)'N')
                    .TextTuple("streamed")
                    .Build()));
        var stop = Assert.IsType<BlueTuskPgOutputStreamStop>(
            decoder.Decode(new byte[] { (byte)'E' }));
        var commit = Assert.IsType<BlueTuskPgOutputStreamCommit>(
            decoder.Decode(
                new PayloadBuilder('c')
                    .UInt32(42)
                    .Byte(0)
                    .UInt64(100)
                    .UInt64(101)
                    .Int64(0)
                    .Build()));

        Assert.True(start.IsFirstSegment);
        Assert.Equal(42U, insert.StreamingTransactionId);
        Assert.NotNull(stop);
        Assert.Equal(42U, commit.TransactionId);
        Assert.False(decoder.IsInsideStreamSegment);
    }

    [Fact]
    public void Decodes_parallel_stream_abort_metadata()
    {
        var decoder = new BlueTuskPgOutputDecoder(
            new BlueTuskPgOutputDecoderOptions
            {
                ProtocolVersion = 4,
                StreamingMode = BlueTuskPgOutputStreamingMode.Parallel,
            });

        var abort = Assert.IsType<BlueTuskPgOutputStreamAbort>(
            decoder.Decode(
                new PayloadBuilder('A')
                    .UInt32(42)
                    .UInt32(43)
                    .UInt64(100)
                    .Int64(3_000_000)
                    .Build()));

        Assert.Equal(42U, abort.TransactionId);
        Assert.Equal(43U, abort.SubtransactionId);
        Assert.Equal(100UL, abort.AbortPosition!.Value.Value);
        Assert.Equal(
            new DateTimeOffset(2000, 1, 1, 0, 0, 3, TimeSpan.Zero),
            abort.AbortTimestamp);
    }

    [Fact]
    public void Decodes_all_two_phase_message_families()
    {
        var decoder = new BlueTuskPgOutputDecoder(
            new BlueTuskPgOutputDecoderOptions
            {
                ProtocolVersion = 3,
                StreamingMode = BlueTuskPgOutputStreamingMode.On,
                TwoPhase = true,
            });

        var begin = Assert.IsType<BlueTuskPgOutputBeginPrepare>(
            decoder.Decode(TwoPhasePayload('b', includeFlags: false, includeRollback: false)));
        var prepare = Assert.IsType<BlueTuskPgOutputPrepare>(
            decoder.Decode(TwoPhasePayload('P', includeFlags: true, includeRollback: false)));
        var commit = Assert.IsType<BlueTuskPgOutputCommitPrepared>(
            decoder.Decode(TwoPhasePayload('K', includeFlags: true, includeRollback: false)));
        var rollback = Assert.IsType<BlueTuskPgOutputRollbackPrepared>(
            decoder.Decode(TwoPhasePayload('r', includeFlags: true, includeRollback: true)));
        var streamPrepare = Assert.IsType<BlueTuskPgOutputStreamPrepare>(
            decoder.Decode(TwoPhasePayload('p', includeFlags: true, includeRollback: false)));

        Assert.Equal("gid-42", begin.GlobalTransactionId);
        Assert.Equal(42U, prepare.TransactionId);
        Assert.Equal(10UL, commit.CommitPosition.Value);
        Assert.Equal(
            new DateTimeOffset(2000, 1, 1, 0, 0, 2, TimeSpan.Zero),
            rollback.RollbackTimestamp);
        Assert.Equal(11UL, streamPrepare.TransactionEndPosition.Value);
    }

    [Theory]
    [InlineData(1, BlueTuskPgOutputStreamingMode.On, false)]
    [InlineData(3, BlueTuskPgOutputStreamingMode.Parallel, false)]
    [InlineData(2, BlueTuskPgOutputStreamingMode.On, true)]
    public void Rejects_incompatible_decoder_options(
        int version,
        BlueTuskPgOutputStreamingMode streamingMode,
        bool twoPhase)
    {
        Assert.Throws<ArgumentException>(
            () => new BlueTuskPgOutputDecoder(
                new BlueTuskPgOutputDecoderOptions
                {
                    ProtocolVersion = version,
                    StreamingMode = streamingMode,
                    TwoPhase = twoPhase,
                }));
    }

    [Fact]
    public void Rejects_unknown_truncated_and_state_incompatible_messages()
    {
        var decoder = new BlueTuskPgOutputDecoder();

        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(new byte[] { (byte)'?' }));
        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(new byte[] { (byte)'B' }));
        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(new byte[] { (byte)'E' }));
        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(
                new PayloadBuilder('C')
                    .Byte(1)
                    .UInt64(1)
                    .UInt64(2)
                    .Int64(0)
                    .Build()));
    }

    [Fact]
    public void Rejects_collection_counts_above_the_parser_ceiling()
    {
        var decoder = new BlueTuskPgOutputDecoder();

        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(
                new PayloadBuilder('R')
                    .UInt32(1)
                    .CString("public")
                    .CString("items")
                    .Byte((byte)'d')
                    .Int16(4097)
                    .Build()));
        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(
                new PayloadBuilder('I')
                    .UInt32(1)
                    .Byte((byte)'N')
                    .Int16(4097)
                    .Build()));
        Assert.Throws<BlueTuskPgOutputProtocolException>(
            () => decoder.Decode(
                new PayloadBuilder('T')
                    .Int32(4097)
                    .Byte(0)
                    .Build()));
    }

    [Fact]
    public async Task Async_extension_preserves_the_xlog_envelope()
    {
        var data = new PayloadBuilder('B')
            .UInt64(10)
            .Int64(0)
            .UInt32(42)
            .Build();
        var messages = GetReplicationMessages(data);

        var envelopes = new List<BlueTuskPgOutputEnvelope>();
        await foreach (var item in messages.DecodePgOutputAsync())
        {
            envelopes.Add(item);
        }

        var envelope = Assert.Single(envelopes);
        Assert.Equal(100UL, envelope.XLogData.WalStart.Value);
        Assert.IsType<BlueTuskPgOutputBegin>(envelope.Message);
    }

    private static byte[] TwoPhasePayload(
        char code,
        bool includeFlags,
        bool includeRollback)
    {
        var builder = new PayloadBuilder(code);
        if (includeFlags)
        {
            builder.Byte(0);
        }

        builder.UInt64(10).UInt64(11).Int64(1_000_000);
        if (includeRollback)
        {
            builder.Int64(2_000_000);
        }

        return builder.UInt32(42).CString("gid-42").Build();
    }

    private static async IAsyncEnumerable<BlueTuskReplicationMessage> GetReplicationMessages(
        byte[] data)
    {
        await Task.Yield();
        yield return new BlueTuskPrimaryKeepalive(
            new BlueTuskLogSequenceNumber(100),
            DateTimeOffset.UnixEpoch,
            ReplyRequested: false);
        yield return new BlueTuskXLogData(
            new BlueTuskLogSequenceNumber(100),
            new BlueTuskLogSequenceNumber(200),
            DateTimeOffset.UnixEpoch,
            data);
    }

    private sealed class PayloadBuilder
    {
        private readonly List<byte> _bytes;

        public PayloadBuilder(char code)
        {
            _bytes = [(byte)code];
        }

        public PayloadBuilder Byte(byte value)
        {
            _bytes.Add(value);
            return this;
        }

        public PayloadBuilder Int16(short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            _bytes.AddRange(bytes.ToArray());
            return this;
        }

        public PayloadBuilder Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            _bytes.AddRange(bytes.ToArray());
            return this;
        }

        public PayloadBuilder UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _bytes.AddRange(bytes.ToArray());
            return this;
        }

        public PayloadBuilder Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            _bytes.AddRange(bytes.ToArray());
            return this;
        }

        public PayloadBuilder UInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            _bytes.AddRange(bytes.ToArray());
            return this;
        }

        public PayloadBuilder CString(string value)
        {
            _bytes.AddRange(Encoding.UTF8.GetBytes(value));
            _bytes.Add(0);
            return this;
        }

        public PayloadBuilder Value(string value) =>
            Value(Encoding.UTF8.GetBytes(value));

        public PayloadBuilder Value(byte[] value)
        {
            Int32(value.Length);
            _bytes.AddRange(value);
            return this;
        }

        public PayloadBuilder TextTuple(string value) =>
            Int16(1).Byte((byte)'t').Value(value);

        public byte[] Build() => [.. _bytes];
    }
}
