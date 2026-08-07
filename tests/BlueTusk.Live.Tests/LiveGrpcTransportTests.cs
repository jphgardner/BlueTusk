using System.Text.Json;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.Grpc;
using BlueTusk.Live.Grpc.Protocol;

namespace BlueTusk.Live.Tests;

public sealed class LiveGrpcTransportTests
{
    [Fact]
    public void Mapper_preserves_sequence_token_and_versioned_json_event()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "sequence": 12,
              "kind": "ResultReset",
              "rows": [{ "id": 7 }],
              "order": [7],
              "resetReason": "ReplayExpired"
            }
            """);
        var message = CreateTransportMessage(
            LiveSubscriberMessageKind.Event,
            12,
            "signed-token",
            document.RootElement.Clone());

        var mapped = LiveGrpcMessageMapper.Map(message);

        Assert.Equal(LiveGrpcMessageKind.Event, mapped.Kind);
        Assert.Equal(12, mapped.Sequence);
        Assert.Equal("signed-token", mapped.ResumeToken);
        using var mappedJson = JsonDocument.Parse(mapped.EventJson);
        Assert.Equal("ResultReset", mappedJson.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Mapper_emits_explicit_reset_control_without_fake_sequence()
    {
        var message = CreateTransportMessage(
            LiveSubscriberMessageKind.ResetRequired,
            null,
            null,
            null);

        var mapped = LiveGrpcMessageMapper.Map(message);

        Assert.Equal(LiveGrpcMessageKind.ResetRequired, mapped.Kind);
        Assert.Equal(0, mapped.Sequence);
        Assert.Empty(mapped.ResumeToken);
        Assert.Empty(mapped.EventJson);
    }

    private static LiveTransportMessage CreateTransportMessage(
        LiveSubscriberMessageKind kind,
        long? sequence,
        string? token,
        JsonElement? payload)
    {
        var constructor = typeof(LiveTransportMessage).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        return (LiveTransportMessage)constructor.Invoke([kind, sequence, token, payload]);
    }
}
