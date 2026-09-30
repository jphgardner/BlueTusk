using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BlueTusk.Events;

/// <summary>A stable event wire contract. Supply source-generated metadata for trimming and NativeAOT.</summary>
public sealed class EventContract<T>
{
    public EventContract(string name, int version, JsonTypeInfo<T> jsonTypeInfo)
    {
        EventValidation.Key(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        Name = name;
        Version = version;
        JsonTypeInfo = jsonTypeInfo;
    }

    public string Name { get; }
    public int Version { get; }
    public JsonTypeInfo<T> JsonTypeInfo { get; }

    public EventWrite Create(Guid eventId, T value, DateTimeOffset occurredAt) =>
        new(eventId, Name, Version, occurredAt, JsonSerializer.SerializeToUtf8Bytes(value, JsonTypeInfo));

    public T? Deserialize(StoredEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.EventType, Name, StringComparison.Ordinal) || value.Version != Version)
        {
            throw new InvalidOperationException($"Expected event contract {Name} v{Version}, received {value.EventType} v{value.Version}.");
        }

        return JsonSerializer.Deserialize(value.Payload.Span, JsonTypeInfo);
    }
}
