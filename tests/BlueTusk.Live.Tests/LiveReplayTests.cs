namespace BlueTusk.Live.Tests;

public sealed class LiveReplayTests
{
    [Fact]
    public void Json_envelopes_are_integrity_checked_and_append_sequences_are_contiguous()
    {
        var initial = LiveResultDiffer.Initial<Row, int>([new Row(1, "one")], static row => row.Id);
        var replayEvent = LiveReplayJsonSerializer.Serialize(Assert.Single(initial.Events));

        Assert.Equal(LiveReplayJsonSerializer.ContentType, replayEvent.ContentType);
        Assert.True(LiveReplayJsonSerializer.VerifyIntegrity(replayEvent));
        Assert.Throws<ArgumentException>(() => new LiveReplayAppendRequest(
            Identity(),
            1,
            [replayEvent]));
    }

    [Fact]
    public void Public_replay_payload_constructor_defensively_copies_bytes()
    {
        byte[] payload = [1, 2, 3];
        var replayEvent = new LiveReplayEvent(
            1,
            LiveEventKind.InitialResult,
            LiveReplayJsonSerializer.ContentType,
            payload);

        payload[0] = 42;

        Assert.Equal(1, replayEvent.Payload.Span[0]);
        Assert.True(LiveReplayJsonSerializer.VerifyIntegrity(replayEvent));
    }

    private static LiveSubscriptionIdentity Identity() =>
        new(
            "database",
            new string('a', 64),
            new string('b', 64),
            "tenant:a",
            "policy:v1",
            50);

    private sealed record Row(int Id, string Value);
}
