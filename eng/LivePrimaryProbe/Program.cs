using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlueTusk.Benchmarks;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;

const int resultCount = 1_000;
const int subscriberCount = 64;
const string scenario = "affected-key-update";
const string publication = "live_primary_benchmark";
const string slot = "live_primary_benchmark";
const string storageSchema = "live_primary_storage";

if (args is not [var outputPath, var commit, var role, var trial, var warmup, var duration, var samples, var evidenceClass] ||
    role is not ("candidate" or "reference") || evidenceClass is not ("qualification" or "diagnostic"))
{
    throw new ArgumentException("The Live probe requires a raw output path and the matched trial identity.");
}

var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
    throw new PlatformNotSupportedException("The Live primary comparison requires Windows or Linux.");
if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    throw new PlatformNotSupportedException("The Live primary comparison requires x64.");
}

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_CONNECTION_STRING");
ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
var warmupSeconds = int.Parse(warmup, CultureInfo.InvariantCulture);
var measurementSeconds = int.Parse(duration, CultureInfo.InvariantCulture);
var sampleCapacity = int.Parse(samples, CultureInfo.InvariantCulture);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180 + warmupSeconds + measurementSeconds));
var token = deadline.Token;
await using var source = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var writer = (BlueTuskConnection)await source.OpenConnectionAsync(token);
await using var reader = (BlueTuskConnection)await source.OpenConnectionAsync(token);

// The capture launcher owns a new disposable database. Each process resets its own trial data.
await using (var initialize = (BlueTuskCommand)writer.CreateCommand())
{
    initialize.CommandText = """
        DROP PUBLICATION IF EXISTS live_primary_benchmark;
        DROP SCHEMA IF EXISTS live_primary_storage CASCADE;
        DROP TABLE IF EXISTS public.live_primary_results;
        CREATE TABLE public.live_primary_results (id int PRIMARY KEY, revision int NOT NULL);
        INSERT INTO public.live_primary_results SELECT id, 0 FROM generate_series(1,1000) AS seed(id);
        CREATE PUBLICATION live_primary_benchmark FOR TABLE public.live_primary_results;
        """;
    _ = await initialize.ExecuteNonQueryAsync(token);
}

await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString, token);
var identity = await replication.IdentifySystemAsync(token);
var database = identity.DatabaseName ?? throw new InvalidOperationException("The Live fixture has no database identity.");
var sourceIdentity = new ChangeSourceIdentity(identity.SystemIdentifier, database, slot, publication);
_ = await replication.CreateReplicationSlotAsync(slot, temporary: true, cancellationToken: token);
var envelopes = replication.StartReplicationAsync(new BlueTuskPgOutputReplicationOptions
{
    SlotName = slot,
    PublicationNames = [publication],
}, token).DecodePgOutputAsync(cancellationToken: token);
var stream = new PgOutputChangeStream(envelopes, sourceIdentity);
await using var deliveries = stream.ReadTransactionsAsync(token).GetAsyncEnumerator(token);

var store = new PostgreSqlLiveInvalidationStore(new() { ControlDataSource = source, ControlSchema = storageSchema });
var consumer = new LiveInvalidationConsumer(database, store);
var plan = new LiveQueryPlan<ResultRow, int>(
    "live-primary", database, new string('a', 64),
    LiveQueryCapabilities.SingleTable | LiveQueryCapabilities.ParameterizedPredicate |
    LiveQueryCapabilities.DeterministicOrdering | LiveQueryCapabilities.BoundedTake,
    [new LiveTableDependency("public", "live_primary_results")], [], resultCount,
    ReadResultAsync, static item => item.Id);
var session = new LiveQuerySession<ResultRow, int>(
    plan, plan.Bind(new Dictionary<string, object?>()), new LiveSecurityScope("benchmark", "v1"), store);
await using var shared = new LiveSharedSubscription<ResultRow, int>(session, store, new()
{
    MaximumSubscribers = subscriberCount,
    SubscriberBufferCapacity = 1,
});
await shared.StartAsync(token);
if (shared.Status.QuerySession.ResultCount != resultCount || shared.Status.PersistedSequence != 1)
{
    throw new InvalidDataException("The Live query did not start with the approved result count and persisted initial event.");
}

var subscriptions = new List<LiveSubscriptionConnection>(subscriberCount);
var readers = new List<IAsyncEnumerator<LiveSubscriberMessage>>(subscriberCount);
var pendingReads = new Task<bool>?[subscriberCount];
var revision = 0;
try
{
    for (var index = 0; index < subscriberCount; index++)
    {
        var opened = await shared.ConnectAsync(1, token);
        if (opened.Connection is not { Replay.Count: 0 } connection)
        {
            throw new InvalidDataException("A Live subscriber failed to connect at the initial sequence.");
        }
        subscriptions.Add(connection);
        readers.Add(connection.ReadAllAsync(token).GetAsyncEnumerator(token));
    }

    _ = await ProviderRequestCapture.MeasureWindowAsync([UpdateAndDeliverAsync], warmupSeconds, 0, token);
    var before = shared.Status;
    var measured = await ProviderRequestCapture.MeasureWindowAsync([UpdateAndDeliverAsync], measurementSeconds, sampleCapacity, token);
    var after = shared.Status;
    var deliveriesObserved = after.FanOutDeliveries - before.FanOutDeliveries;
    if (after.SubscriberCount != subscriberCount || after.QuerySession.ResultCount != resultCount ||
        deliveriesObserved != measured.CompletedOperations * subscriberCount ||
        after.PersistedSequence - before.PersistedSequence != measured.CompletedOperations)
    {
        throw new InvalidDataException("The Live trial did not persist and deliver each completed change to every subscriber.");
    }

    var implementation = typeof(LiveQuerySession<ResultRow, int>).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    var report = new
    {
        SchemaVersion = 1,
        EvidenceKind = "bluetusk-performance-trial",
        Family = "Live",
        WorkloadKey = $"{os}|Live|primary-hot-path",
        Role = role,
        Trial = int.Parse(trial, CultureInfo.InvariantCulture),
        SourceCommit = commit,
        Diagnostic = evidenceClass == "diagnostic",
        Synthetic = false,
        Implementation = implementation,
        WarmupSeconds = warmupSeconds,
        MeasurementSeconds = measurementSeconds,
        Environment = new
        {
            Os = os,
            Architecture = "x64",
            Runtime = RuntimeInformation.FrameworkDescription,
            Scenario = scenario,
            ResultCount = resultCount,
            SubscriberCount = subscriberCount,
            Source = "postgresql-pgoutput",
            Replay = "postgresql",
            ServerVersion = reader.ServerVersion,
        },
        DeliveriesObserved = deliveriesObserved,
        PersistedUpdates = after.PersistedSequence - before.PersistedSequence,
        Stopwatch.Frequency,
        Measurement = measured with
        {
            Workers = measured.Workers.Select(worker => new ProviderRequestCapture.WorkerSamples(
                worker.RequestTicks.AsSpan(0, worker.Count).ToArray(), worker.Count)).ToArray(),
        },
    };
    await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
}
finally
{
    foreach (var connection in subscriptions) await connection.DisposeAsync();
    foreach (var enumerator in readers) await enumerator.DisposeAsync();
}

async Task UpdateAndDeliverAsync(CancellationToken cancellationToken)
{
    var expectedRevision = ++revision;
    var expectedKey = (expectedRevision - 1) % resultCount + 1;
    var expectedSequence = expectedRevision + 1L;
    await using (var update = (BlueTuskCommand)writer.CreateCommand())
    {
        update.CommandText = "UPDATE public.live_primary_results SET revision = $1 WHERE id = $2";
        update.Parameters.Add(new BlueTuskParameter<int>(expectedRevision));
        update.Parameters.Add(new BlueTuskParameter<int>(expectedKey));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException("The Live writer failed to commit exactly one affected key.");
        }
    }

    if (!await deliveries.MoveNextAsync())
    {
        throw new EndOfStreamException("PostgreSQL replication ended before delivering the committed update.");
    }

    await using (var delivery = deliveries.Current)
    {
        if (delivery.Transaction.Changes.Count != 1)
        {
            throw new InvalidDataException("The Live workload expected one change per committed transaction.");
        }
        await consumer.ConsumeTransactionAsync(delivery, cancellationToken);
    }

    for (var index = 0; index < readers.Count; index++) pendingReads[index] = readers[index].MoveNextAsync().AsTask();
    try
    {
        if (await shared.RefreshAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException("The committed update did not publish exactly one Live event.");
        }
        ReadOnlyMemory<byte> payload = default;
        for (var index = 0; index < readers.Count; index++)
        {
            var next = pendingReads[index]!;
            pendingReads[index] = null;
            if (!await next || readers[index].Current is not
                    { Kind: LiveSubscriberMessageKind.Event, Event: { Kind: LiveEventKind.RowUpdated } observed } ||
                observed.Sequence != expectedSequence)
            {
                throw new InvalidDataException("A Live subscriber received the wrong event or sequence.");
            }

            if (index == 0)
            {
                payload = observed.Payload;
                using var document = JsonDocument.Parse(payload);
                var update = document.RootElement;
                if (update.GetProperty("key").GetInt32() != expectedKey ||
                    update.GetProperty("row").GetProperty("Id").GetInt32() != expectedKey ||
                    update.GetProperty("row").GetProperty("Revision").GetInt32() != expectedRevision)
                {
                    throw new InvalidDataException("The Live event did not carry the committed key and revision.");
                }
            }
            else if (!observed.Payload.Span.SequenceEqual(payload.Span))
            {
                throw new InvalidDataException("Live subscribers observed inconsistent payloads for the same update.");
            }
        }
    }
    catch
    {
        await deadline.CancelAsync();
        // Complete pending enumerator reads before disposing the subscription.
        foreach (var read in pendingReads)
        {
            try { if (read is not null) _ = await read; }
            catch (OperationCanceledException) { }
        }
        throw;
    }
}

async ValueTask<IReadOnlyList<ResultRow>> ReadResultAsync(LiveQueryExecutionContext context, CancellationToken cancellationToken)
{
    await using var command = (BlueTuskCommand)reader.CreateCommand();
    command.CommandText = "SELECT id, revision FROM public.live_primary_results WHERE id >= $1 ORDER BY id LIMIT $2";
    command.Parameters.Add(new BlueTuskParameter<int>(1));
    command.Parameters.Add(new BlueTuskParameter<int>(resultCount));
    await using var result = await command.ExecuteReaderAsync(cancellationToken);
    var rows = new List<ResultRow>(resultCount);
    while (await result.ReadAsync(cancellationToken)) rows.Add(new(result.GetInt32(0), result.GetInt32(1)));
    if (rows.Count != resultCount) throw new InvalidDataException("The Live query returned the wrong number of rows.");
    return rows;
}

internal sealed record ResultRow(int Id, int Revision);
