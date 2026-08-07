namespace BlueTusk.Live.Testing;

public sealed record LiveReplayStoreConformanceReport(
    string StoreName,
    int Assertions,
    TimeSpan Elapsed);

public static class LiveReplayStoreConformance
{
    public static async ValueTask<LiveReplayStoreConformanceReport> RunAsync(
        ILiveReplayStore store,
        string storeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        var started = TimeProvider.System.GetTimestamp();
        var assertions = 0;
        var identity = Identity("scope:a");
        var first = Event(1, """{"sequence":1}""");
        var append = await store.AppendAsync(
            new LiveReplayAppendRequest(identity, 0, [first]),
            cancellationToken).ConfigureAwait(false);
        Require(append.Status is LiveReplayAppendStatus.Stored, "The initial event was not stored.");
        assertions++;

        var read = await store.ReadAsync(identity, 0, 10, cancellationToken).ConfigureAwait(false);
        Require(
            read.Status is LiveReplayReadStatus.Available &&
            read.Events.Count == 1 &&
            read.Events[0].Sequence == 1,
            "The initial event did not round-trip.");
        assertions++;

        var retry = await store.AppendAsync(
            new LiveReplayAppendRequest(identity, 0, [first]),
            cancellationToken).ConfigureAwait(false);
        Require(retry.Status is LiveReplayAppendStatus.AlreadyStored, "A byte-identical retry was not idempotent.");
        assertions++;

        var divergent = await store.AppendAsync(
            new LiveReplayAppendRequest(identity, 0, [Event(1, """{"sequence":1,"changed":true}""")]),
            cancellationToken).ConfigureAwait(false);
        Require(
            divergent.Status is LiveReplayAppendStatus.SequenceConflict,
            "A divergent retry was not rejected.");
        assertions++;

        var next = Event(2, """{"sequence":2}""");
        var advanced = await store.AppendAsync(
            new LiveReplayAppendRequest(identity, 1, [next]),
            cancellationToken).ConfigureAwait(false);
        Require(
            advanced.Status is LiveReplayAppendStatus.Stored &&
            advanced.CurrentLastSequence == 2,
            "A contiguous append did not advance the sequence.");
        assertions++;

        var limited = await store.ReadAsync(identity, 0, 1, cancellationToken).ConfigureAwait(false);
        Require(
            limited.Events.Count == 1 &&
            limited.LastSequence == 2,
            "A bounded read did not retain the true last sequence.");
        assertions++;

        var isolated = await store.ReadAsync(
            Identity("scope:b"),
            0,
            10,
            cancellationToken).ConfigureAwait(false);
        Require(isolated.Status is LiveReplayReadStatus.NotFound, "Security scopes shared replay state.");
        assertions++;

        return new LiveReplayStoreConformanceReport(
            storeName,
            assertions,
            TimeProvider.System.GetElapsedTime(started));
    }

    private static LiveSubscriptionIdentity Identity(string scope) =>
        new(
            "database",
            new string('a', 64),
            new string('b', 64),
            scope,
            "policy:v1",
            10);

    private static LiveReplayEvent Event(long sequence, string json) =>
        new(
            sequence,
            sequence == 1 ? LiveEventKind.InitialResult : LiveEventKind.RowUpdated,
            LiveReplayJsonSerializer.ContentType,
            System.Text.Encoding.UTF8.GetBytes(json));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new LiveReplayStoreConformanceException(message);
        }
    }
}

public sealed class LiveReplayStoreConformanceException : Exception
{
    public LiveReplayStoreConformanceException(string message)
        : base(message)
    {
    }
}
