using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;

// This source is compiled twice, once against each immutable Jobs source tree.
// The processes share only PostgreSQL and small coordination files, never CLR types.
if (args.Length != 5 || args[0] is not ("seed" or "advance" or "rollback"))
    throw new ArgumentException("Expected phase, schema, state path, signal directory, report path.");

string phase = args[0];
string schema = args[1];
string statePath = args[2];
string signals = args[3];
string reportPath = args[4];
string connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("An owned disposable PostgreSQL connection is required.");
var scope = new JobScope("upgrade-probe", "release");
await using var dataSource = BlueTuskDataSource.Create(connectionString);
var store = new PostgreSqlJobStore(dataSource, new JobStoreOptions { Schema = schema });
await store.InitializeAsync();

await using (var connection = await dataSource.OpenConnectionAsync())
await using (var command = new BlueTuskCommand(
    $"SELECT format_version, maximum_payload_bytes, maximum_history_entries FROM \"{schema}\".settings", connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    Require(await reader.ReadAsync() && reader.GetInt32(0) == 1 &&
        reader.GetInt32(1) == 1_048_576 && reader.GetInt32(2) == 32 &&
        !await reader.ReadAsync(), "Only unchanged durable format-one limits support rollback.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task WaitFor(string path)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    while (!File.Exists(path))
    {
        timeout.Token.ThrowIfCancellationRequested();
        await Task.Delay(100, timeout.Token);
    }
}

static void Signal(string path) => File.WriteAllText(path, "ready\n");

JobRequest Request(string type) => new()
{
    Scope = scope,
    JobType = type,
    Payload = Encoding.UTF8.GetBytes(type),
    DeduplicationKey = type,
    MaximumAttempts = 3,
};

async Task<JobLease> Claim(string type, string owner)
{
    var leases = await store.ClaimAsync(scope, owner, 1, TimeSpan.FromMinutes(4), [type]);
    Require(leases.Count == 1, $"Expected one {type} lease for {owner}.");
    return leases[0];
}

async Task Check(Guid id, JobStatus status, int attempts, params string[] outcomes)
{
    var snapshot = await store.ReadAsync(scope, id);
    Require(snapshot is not null && snapshot.Status == status && snapshot.Attempts == attempts,
        $"Unexpected durable state for {id}.");
    var history = await store.ReadHistoryAsync(scope, id);
    Require(history.Count == outcomes.Length &&
        history.OrderBy(x => x.Attempt).Select(x => x.Outcome).SequenceEqual(outcomes),
        $"Unexpected durable attempt history for {id}.");
}

ProbeState ReadState() => JsonSerializer.Deserialize<ProbeState>(File.ReadAllText(statePath))
    ?? throw new InvalidOperationException("Missing old-binary state.");

if (phase == "seed")
{
    var held = await store.EnqueueAsync(Request("probe.held"));
    var retry = await store.EnqueueAsync(Request("probe.retry"));
    var pending = await store.EnqueueAsync(Request("probe.pending"));
    var heldLease = await Claim("probe.held", "old-owner");
    var retryLease = await Claim("probe.retry", "old-retry");
    Require(await store.FailAsync(retryLease, "retry", TimeSpan.Zero), "Old retry was not persisted.");
    await Check(held, JobStatus.Running, 1, "running");
    await Check(retry, JobStatus.Pending, 1, "failed");
    await Check(pending, JobStatus.Pending, 0);
    File.WriteAllText(statePath, JsonSerializer.Serialize(new ProbeState(
        held, retry, pending, heldLease.FencingToken, retryLease.FencingToken)));
    Signal(Path.Combine(signals, "old-ready"));
    await WaitFor(Path.Combine(signals, "candidate-ready"));
    Require(await store.CompleteAsync(heldLease), "Old held lease could not complete during overlap.");
    Require(!await store.CompleteAsync(heldLease), "Old held lease produced duplicate completion.");
    await Check(held, JobStatus.Succeeded, 1, "succeeded");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new PhaseReport("seed", true, 1, 0, 0, 1)));
    Signal(Path.Combine(signals, "old-done"));
}
else if (phase == "advance")
{
    var state = ReadState();
    Require(await store.EnqueueAsync(Request("probe.held")) == state.Held &&
        await store.EnqueueAsync(Request("probe.retry")) == state.Retry &&
        await store.EnqueueAsync(Request("probe.pending")) == state.Pending,
        "Candidate changed persisted deduplication identities.");
    await Check(state.Held, JobStatus.Running, 1, "running");
    Require((await store.ReadAsync(scope, state.Held))!.FencingToken == state.HeldFence,
        "Candidate disturbed the old process's in-flight fence.");
    var retryLease = await Claim("probe.retry", "candidate-retry");
    Require(retryLease.Attempt == 2 && retryLease.FencingToken > state.RetryFence,
        "Candidate did not inherit the persisted retry and fence.");
    Require(await store.CompleteAsync(retryLease) && !await store.CompleteAsync(retryLease),
        "Candidate retry completion was missing or duplicated.");
    var pendingLease = await Claim("probe.pending", "candidate-pending");
    Require(await store.CompleteAsync(pendingLease) && !await store.CompleteAsync(pendingLease),
        "Candidate pending completion was missing or duplicated.");
    await Check(state.Retry, JobStatus.Succeeded, 2, "failed", "succeeded");
    await Check(state.Pending, JobStatus.Succeeded, 1, "succeeded");
    Signal(Path.Combine(signals, "candidate-ready"));
    await WaitFor(Path.Combine(signals, "old-done"));
    await Check(state.Held, JobStatus.Succeeded, 1, "succeeded");
    Require((await store.ReadAsync(scope, state.Held))!.FencingToken == state.HeldFence,
        "Old held lease fence changed during rolling overlap.");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new PhaseReport("advance", true, 0, 2, 0, 1)));
}
else
{
    var state = ReadState();
    await Check(state.Held, JobStatus.Succeeded, 1, "succeeded");
    Require((await store.ReadAsync(scope, state.Held))!.FencingToken == state.HeldFence,
        "Rollback did not retain the old held lease fence.");
    await Check(state.Retry, JobStatus.Succeeded, 2, "failed", "succeeded");
    await Check(state.Pending, JobStatus.Succeeded, 1, "succeeded");
    Require(await store.EnqueueAsync(Request("probe.retry")) == state.Retry,
        "Rollback changed deduplication identity.");
    var rollback = await store.EnqueueAsync(Request("probe.rollback"));
    var lease = await Claim("probe.rollback", "old-rollback");
    Require(await store.CompleteAsync(lease) && !await store.CompleteAsync(lease),
        "Old rollback completion was missing or duplicated.");
    await Check(rollback, JobStatus.Succeeded, 1, "succeeded");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new PhaseReport("rollback", true, 0, 0, 1, 1)));
}

internal sealed record ProbeState(Guid Held, Guid Retry, Guid Pending, long HeldFence, long RetryFence);
internal sealed record PhaseReport(string Phase, bool Passed, int OldOverlapEffects,
    int CandidateEffects, int RollbackEffects, int FormatVersion);
