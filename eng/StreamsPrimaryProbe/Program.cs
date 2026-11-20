using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlueTusk.Benchmarks;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;
using BlueTusk.Sync.PostgreSql;

const string publication = "streams_primary_benchmark";
const string slot = "streams_primary_benchmark";
const string baseline = "1.0.0+7380d7b028c72b2aae348b778711d104d022a3f8";

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_CONNECTION_STRING");
ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
if (args is ["--write"])
{
    await StreamsPrimaryWriter.RunAsync(connectionString, CancellationToken.None);
    return;
}

if (args is not [var outputPath, var commit, var role, var trial, var warmup, var duration, var samples, var evidenceClass] ||
    role is not ("candidate" or "reference") || evidenceClass is not ("qualification" or "diagnostic"))
{
    throw new ArgumentException("The Streams probe requires a raw output path and the matched trial identity.");
}
var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
    throw new PlatformNotSupportedException("The Streams primary comparison requires Windows or Linux.");
if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    throw new PlatformNotSupportedException("The Streams primary comparison requires x64.");
}

var family = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_CHANGE_FAMILY") ?? "Streams";
if (family is not ("Streams" or "Sync")) throw new ArgumentException("Select the Streams or PostgreSQL Sync workload.");
var writerAssembly = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_WRITER_ASSEMBLY");
ArgumentException.ThrowIfNullOrWhiteSpace(writerAssembly);
var warmupSeconds = int.Parse(warmup, CultureInfo.InvariantCulture);
var measurementSeconds = int.Parse(duration, CultureInfo.InvariantCulture);
var sampleCapacity = int.Parse(samples, CultureInfo.InvariantCulture);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180 + warmupSeconds + measurementSeconds));
var token = deadline.Token;

await using var control = new BlueTuskConnection(connectionString);
await control.OpenAsync(token);
await using (var initialize = control.CreateCommand())
{
    initialize.CommandText = """
        DROP PUBLICATION IF EXISTS streams_primary_benchmark;
        DROP TABLE IF EXISTS public.streams_primary_source;
        CREATE TABLE public.streams_primary_source (id bigint PRIMARY KEY);
        CREATE PUBLICATION streams_primary_benchmark FOR TABLE public.streams_primary_source;
        """;
    _ = await initialize.ExecuteNonQueryAsync(token);
}

await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString, token);
var identity = await replication.IdentifySystemAsync(token);
var database = identity.DatabaseName ?? throw new InvalidOperationException("The Streams fixture has no database identity.");
var source = new ChangeSourceIdentity(identity.SystemIdentifier, database, slot, publication);
_ = await replication.CreateReplicationSlotAsync(slot, temporary: true, cancellationToken: token);
var envelopes = replication.StartReplicationAsync(new BlueTuskPgOutputReplicationOptions
{
    SlotName = slot,
    PublicationNames = [publication],
}, token).DecodePgOutputAsync(cancellationToken: token);
var stream = new PgOutputChangeStream(envelopes, source);
await using var transactions = stream.ReadTransactionsAsync(token).GetAsyncEnumerator(token);
await using var sync = family == "Sync" ? await PrimaryPostgreSqlSync.OpenAsync(control, connectionString, source, token) : null;
await using var writer = new StreamsPrimaryWriter(writerAssembly);
if (await writer.ReadAsync(token) != baseline)
{
    throw new InvalidDataException("Candidate and reference must use the same released PostgreSQL writer.");
}

var nextKey = 0L;
BlueTuskLogSequenceNumber? previousPosition = null;
uint lastTransactionId = 0;
long[] wallTicks = [];
var sampleCount = 0;
_ = await ProviderRequestCapture.MeasureWindowAsync([ReadOneChangeAsync], warmupSeconds, 0, token);
var clockBefore = await SampleClockAsync();
wallTicks = new long[sampleCapacity];
var measurement = await ProviderRequestCapture.MeasureWindowAsync([ReadOneChangeAsync], measurementSeconds, 0, token);
var clockAfter = await SampleClockAsync();
if (sync is not null)
{
    await sync.VerifyAsync(nextKey, previousPosition!.Value, lastTransactionId, token);
}
if (sampleCount != measurement.CompletedOperations)
{
    throw new InvalidDataException("Every completed Streams transaction must retain a raw delivery sample.");
}
var offsetUpperBound = Math.Max(clockBefore.LatestOffsetTicks, clockAfter.LatestOffsetTicks);
// A qualification result must hold across the captured offset interval. Use the
// slower bound for the candidate and the faster bound for the released baseline.
var appliedOffset = evidenceClass == "qualification" && role == "reference"
    ? Math.Min(clockBefore.EarliestOffsetTicks, clockAfter.EarliestOffsetTicks)
    : offsetUpperBound;
var requestTicks = wallTicks.AsSpan(0, sampleCount).ToArray();
for (var index = 0; index < requestTicks.Length; index++)
{
    requestTicks[index] = checked(requestTicks[index] + appliedOffset);
    if (requestTicks[index] <= 0)
    {
        throw new InvalidDataException("The Streams commit-to-delivery latency could not be resolved.");
    }
}
var implementationType = sync is null ? typeof(PgOutputChangeStream) : typeof(PostgreSqlSyncDestination);
var implementation = implementationType.Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
var report = new
{
    SchemaVersion = 1,
    EvidenceKind = "bluetusk-performance-trial",
    Family = family,
    WorkloadKey = $"{os}|{family}|primary-hot-path",
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
        Scenario = "commit-to-delivery",
        ChangesPerTransaction = 1,
        Destination = sync is null ? null : "postgresql",
        MutationsPerTransaction = sync is null ? (int?)null : 1,
        SourceWriterImplementation = baseline,
        Source = "postgresql-pgoutput",
        ServerVersion = control.ServerVersion,
    },
    VerifiedPersistedMutations = sync is null ? (int?)null : sampleCount,
    DestinationCheckpoint = sync is null ? null : previousPosition!.Value.Value.ToString(CultureInfo.InvariantCulture),
    DestinationCheckpointTransaction = sync is null ? (uint?)null : lastTransactionId,
    ClockBefore = clockBefore,
    ClockAfter = clockAfter,
    SourceClockUpperOffsetTicks = offsetUpperBound,
    SourceClockAppliedOffsetTicks = appliedOffset,
    RawWallTicksByTransaction = wallTicks.AsSpan(0, sampleCount).ToArray(),
    Frequency = TimeSpan.TicksPerSecond,
    Measurement = measurement with
    {
        ElapsedTicks = Stopwatch.GetElapsedTime(0, measurement.ElapsedTicks).Ticks,
        Workers = [new ProviderRequestCapture.WorkerSamples(requestTicks, requestTicks.Length)],
    },
};
await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions(JsonSerializerDefaults.Web), token);

async Task ReadOneChangeAsync(CancellationToken cancellationToken)
{
    var key = ++nextKey;
    var pending = transactions.MoveNextAsync().AsTask();
    try
    {
        await writer.RequestCommitAsync(key, cancellationToken);
        if (!await pending.WaitAsync(cancellationToken))
        {
            throw new EndOfStreamException("The replication stream ended before delivering its committed change.");
        }
        var observedUtc = DateTimeOffset.UtcNow;
        await using var delivery = transactions.Current;
        var transaction = delivery.Transaction;
        if (transaction.Changes.Count != 1 || transaction.Outcome != ChangeTransactionOutcome.Committed ||
            (previousPosition is { } position && transaction.CommitEndPosition.CompareTo(position) <= 0))
        {
            throw new InvalidDataException("The Streams probe received an unexpected or out-of-order transaction.");
        }
        if (await writer.ReadAsync(cancellationToken) != key.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException("The source did not confirm the committed change.");
        }

        if (sync is null)
        {
            var checkedChange = false;
            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
            {
                if (checkedChange || !StreamsPrimaryChange.IsExpectedInsert(change, key))
                {
                    throw new InvalidDataException("The delivered Streams key does not match the committed write.");
                }
                checkedChange = true;
            }
            if (!checkedChange) throw new InvalidDataException("Streams delivered an empty transaction.");
            await delivery.AcknowledgeAsync(cancellationToken);
        }
        else
        {
            await sync.ApplyAsync(delivery, key, cancellationToken);
            observedUtc = DateTimeOffset.UtcNow;
        }

        previousPosition = transaction.CommitEndPosition;
        lastTransactionId = transaction.TransactionId;
        if (wallTicks.Length == 0) return;
        if (sampleCount == wallTicks.Length)
        {
            throw new InvalidOperationException("Raw sample capacity exhausted. No truncated capture is accepted.");
        }
        wallTicks[sampleCount++] = (observedUtc - transaction.CommitTimestamp).Ticks;
    }
    catch
    {
        await deadline.CancelAsync();
        try { _ = await pending; }
        catch (OperationCanceledException) { }
        throw;
    }
}

async Task<ClockOffset> SampleClockAsync()
{
    await using var command = control.CreateCommand();
    command.CommandText = "SELECT (extract(epoch FROM clock_timestamp()) * 10000000)::bigint";
    ClockOffset? narrowest = null;
    for (var sample = 0; sample < 12; sample++)
    {
        var before = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var server = (long)(await command.ExecuteScalarAsync(token))!;
        var after = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        if (after < before) throw new InvalidDataException("The wall clock moved backwards during calibration.");
        var interval = new ClockOffset(server - after, server - before);
        if (narrowest is null || after - before < narrowest.LatestOffsetTicks - narrowest.EarliestOffsetTicks)
        {
            narrowest = interval;
        }
    }
    return narrowest!;
}

internal sealed record ClockOffset(long EarliestOffsetTicks, long LatestOffsetTicks);
