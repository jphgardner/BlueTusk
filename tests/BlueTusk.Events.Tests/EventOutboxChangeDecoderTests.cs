using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BlueTusk.Events.Streams;
using BlueTusk.Streams;

namespace BlueTusk.Events.Tests;

public sealed class EventOutboxChangeDecoderTests
{
    [Fact]
    public void TextAndBinaryPostgreSqlWireEncodingsProduceIdenticalEventEnvelope()
    {
        var identity = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 9, 27, 12, 34, 56, TimeSpan.Zero).AddTicks(123_450);
        var payload = new byte[] { 0, 127, 128, 255, 92, 65 };
        var text = Row("bluetusk_events", "tenant", "orders", "42", identity.ToString("D"), "order.placed", "2",
            timestamp.ToString("O", CultureInfo.InvariantCulture), "\\x" + Convert.ToHexStringLower(payload));
        var sequence = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(sequence, 42);
        var version = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(version, 2);
        var occurred = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(occurred, (timestamp - new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / 10);
        var table = Table("bluetusk_events");
        var binary = new ChangeRow(table, new[]
        {
            "tenant"u8.ToArray(), "orders"u8.ToArray(), sequence, identity.ToByteArray(bigEndian: true),
            "order.placed"u8.ToArray(), version, occurred, payload
        }.Select(static value => ChangeColumnValue.FromValue(value, ChangeValueEncoding.Binary)));
        var decoder = new EventOutboxChangeDecoder();
        Assert.True(decoder.TryDecodeRow(text, out var first));
        Assert.True(decoder.TryDecodeRow(binary, out var second));
        Assert.Equal(first.Stream, second.Stream);
        Assert.Equal(first.Sequence, second.Sequence);
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(first.EventType, second.EventType);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(timestamp, first.OccurredAt);
        Assert.Equal(first.OccurredAt, second.OccurredAt);
        Assert.Equal(payload, first.Payload.ToArray());
        Assert.Equal(payload, second.Payload.ToArray());
    }

    [Theory]
    [InlineData("\\000\\177\\\\\\012A")]
    [InlineData("\\x007f5c0a41")]
    public void BothPostgreSqlByteaOutputStylesAreDecoded(string payload)
    {
        var row = Row("bluetusk_events", "tenant", "orders", "1", Guid.NewGuid().ToString("D"), "event", "1", "2026-09-27 12:00:00+00", payload);
        Assert.True(new EventOutboxChangeDecoder().TryDecodeRow(row, out var value));
        Assert.Equal(new byte[] { 0, 127, 92, 10, 65 }, value.Payload.ToArray());
    }

    [Theory]
    [InlineData("\\x0")]
    [InlineData("\\xgg")]
    [InlineData("\\400")]
    [InlineData("\\01")]
    [InlineData("\\x")]
    public void MalformedOrEmptyPayloadFailsClosed(string payload)
    {
        var row = Row("bluetusk_events", "tenant", "orders", "1", Guid.NewGuid().ToString("D"), "event", "1", "2026-09-27 12:00:00+00", payload);
        Assert.Throws<EventOutboxDecodingException>(() => new EventOutboxChangeDecoder().TryDecodeRow(row, out _));
    }

    [Fact]
    public void PayloadBoundAndImmutableOutboxContractAreEnforced()
    {
        var row = Row("bluetusk_events", "tenant", "orders", "1", Guid.NewGuid().ToString("D"), "event", "1", "2026-09-27 12:00:00+00", "\\x010203");
        Assert.Throws<EventOutboxDecodingException>(() => new EventOutboxChangeDecoder(maximumEventBytes: 2).TryDecodeRow(row, out _));
        Assert.Throws<InvalidOperationException>(() => new EventOutboxChangeDecoder().TryDecode(new DeleteChange(default, row), out _));
        Assert.Throws<InvalidOperationException>(() => new EventOutboxChangeDecoder().TryDecode(new UpdateChange(default, row, row, new ChangedColumnSet(true, [7])), out _));
        Assert.False(new EventOutboxChangeDecoder("other_schema").TryDecodeRow(row, out _));
    }

    internal static ChangeTable Table(string schema) => new(1, schema, "outbox", 'd', new[]
    {
        ("tenant_id", 25u), ("stream_id", 25u), ("sequence", 20u), ("event_id", 2950u),
        ("event_type", 25u), ("version", 23u), ("occurred_at", 1184u), ("payload", 17u)
    }.Select((entry, ordinal) => new ChangeColumn(ordinal, entry.Item1, entry.Item2, -1, ordinal < 3)));

    internal static ChangeRow Row(string schema, params string[] values) => new(Table(schema),
        values.Select(static value => ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text)));
}
