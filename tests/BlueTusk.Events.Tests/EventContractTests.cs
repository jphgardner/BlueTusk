using System.Text;
using System.Text.Json.Serialization;
using BlueTusk.Data;

namespace BlueTusk.Events.Tests;

public sealed class EventContractTests
{
    [Fact]
    public void SourceGeneratedContractRoundTripsAndChecksWireIdentity()
    {
        var contract = new EventContract<OrderPlaced>("orders.placed", 2, EventTestJson.Default.OrderPlaced);
        var identity = Guid.NewGuid();
        var write = contract.Create(identity, new OrderPlaced(42, 19.95m), DateTimeOffset.UtcNow);
        var stored = new StoredEvent(new EventStreamKey("tenant", "orders"), 1, identity,
            write.EventType, write.Version, write.OccurredAt, write.Payload);
        Assert.Equal(new OrderPlaced(42, 19.95m), contract.Deserialize(stored));
        Assert.Throws<InvalidOperationException>(() => contract.Deserialize(stored with { Version = 1 }));
        Assert.Throws<InvalidOperationException>(() => contract.Deserialize(stored with { EventType = "other" }));
    }

    [Fact]
    public void EventPayloadOwnsItsBytesAndNormalizesTimestamp()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        var write = new EventWrite(Guid.NewGuid(), "event", 1, DateTimeOffset.Now, bytes);
        bytes[0] = 0;
        Assert.Equal("payload", Encoding.UTF8.GetString(write.Payload.Span));
        Assert.Equal(TimeSpan.Zero, write.OccurredAt.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("tenant\0")]
    public void RejectsInvalidTenantKeys(string tenantId) =>
        Assert.ThrowsAny<ArgumentException>(() => new EventStreamKey(tenantId, "orders"));

    [Theory]
    [InlineData("schema; DROP TABLE x")]
    [InlineData("9invalid")]
    [InlineData("\"quoted\"")]
    public void RejectsUnsafeSchemaIdentifiers(string schema)
    {
        using var dataSource = BlueTuskDataSource.Create("Host=localhost;Username=postgres");
        Assert.Throws<ArgumentException>(() => new PostgreSqlEventStore(dataSource, new PostgreSqlEventsOptions { Schema = schema }));
    }

    [Fact]
    public void RejectsEmptyEventIdentityAndInvalidBounds()
    {
        Assert.Throws<ArgumentException>(() => new EventWrite(Guid.Empty, "event", 1, DateTimeOffset.UtcNow, "{}"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventWrite(Guid.NewGuid(), "event", 0, DateTimeOffset.UtcNow, "{}"u8));
        using var dataSource = BlueTuskDataSource.Create("Host=localhost;Username=postgres");
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlEventStore(dataSource,
            new PostgreSqlEventsOptions { MaximumEventBytes = 1024, MaximumAppendBytes = 100 }));
    }
}

public sealed record OrderPlaced(int OrderId, decimal Total);

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class EventTestJson : JsonSerializerContext;
