using System.Diagnostics;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.PhysicalRecoveryTests;

internal sealed class PhysicalRecoveryFixture
{
    internal const string Owner = "bluetusk.jobs.workflows.physical-recovery";
    internal string PrimaryConnection { get; } = Required("PRIMARY");
    internal string StandbyConnection { get; } = Required("STANDBY");
    internal string Fixture { get; } = Required("FIXTURE");
    internal string ReportPath { get; } = Required("REPORT");

    internal BlueTuskDataSource CreateMultihostSource()
    {
        var first = new BlueTuskConnectionStringBuilder(PrimaryConnection);
        var second = new BlueTuskConnectionStringBuilder(StandbyConnection);
        first.Host = first.Host + "," + second.Host;
        first.Ports = first.Port + "," + second.Port;
        first.TargetSessionAttributes = BlueTuskTargetSessionAttributes.ReadWrite;
        first.MaximumPoolSize = 8; // Per-host maximum; the aggregate is sixteen.
        first.MinimumPoolSize = 0;
        first.Timeout = TimeSpan.FromSeconds(1);
        first.ApplicationName = "BlueTuskJobsWorkflowPhysicalRecovery";
        return BlueTuskDataSource.Create(first.ConnectionString);
    }

    internal async Task KillPrimaryAsync(CancellationToken token)
    {
        string name = Required("PRIMARY_CONTAINER");
        await VerifyOwnedAsync(name, token);
        _ = await DockerAsync(["kill", "--signal", "KILL", name], token);
        Assert.Equal("false", (await DockerAsync(["inspect", name, "--format", "{{.State.Running}}"], token)).Trim());
    }

    internal async Task PromoteAsync(CancellationToken token)
    {
        string name = Required("STANDBY_CONTAINER");
        await VerifyOwnedAsync(name, token);
        // The killed primary must stay stopped; no attempt is made to restart or rejoin it.
        Assert.Equal("false", (await DockerAsync(["inspect", Required("PRIMARY_CONTAINER"), "--format", "{{.State.Running}}"], token)).Trim());
        _ = await DockerAsync(["exec", name, "gosu", "postgres", "pg_ctl", "-D", "/var/lib/postgresql/data/pgdata", "promote", "-w", "-t", "30"], token);
    }

    private async Task VerifyOwnedAsync(string name, CancellationToken token)
    {
        string actual = (await DockerAsync(["inspect", name, "--format", "{{index .Config.Labels \"bluetusk.owner\"}}|{{index .Config.Labels \"bluetusk.fixture\"}}"], token)).Trim();
        // The release gate runner may label its fixtures with its own owner; the default is eng/jobs-physical-recovery.ps1.
        string owner = Environment.GetEnvironmentVariable("BLUETUSK_JOBS_RECOVERY_OWNER") is { Length: > 0 } configured ? configured : Owner;
        Assert.Equal(owner + "|" + Fixture, actual);
    }

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Required("DOCKER")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Owned Docker fixture process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            string result = await stdout;
            _ = await stderr;
            Assert.Equal(0, process.ExitCode);
            return result;
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            try { await Task.WhenAll(stdout, stderr); } catch (Exception) { }
            throw;
        }
    }

    internal static string Required(string suffix) => Environment.GetEnvironmentVariable("BLUETUSK_JOBS_RECOVERY_" + suffix) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Run eng/jobs-physical-recovery.ps1; a real owned primary/standby fixture is required.");

    internal static async Task ExecuteAsync(BlueTuskDataSource source, string sql, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(token);
    }

    internal static async Task<T> ScalarAsync<T>(BlueTuskDataSource source, string sql, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand(sql, connection);
        return (await command.ExecuteScalarAsync<T>(token))!;
    }

    internal static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken token, TimeSpan? timeout = null)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            bool satisfied = await condition();
            Assert.True(Stopwatch.GetElapsedTime(started) < (timeout ?? TimeSpan.FromSeconds(45)), "Physical recovery exceeded its bounded phase deadline.");
            if (satisfied) { return; }
            await Task.Delay(50, token);
        }
    }

    // Read the actual owned format-one lease row; never synthesize an ownership token.
    internal static async Task<JobLease> ReadLeaseAsync(BlueTuskDataSource source, string schema, JobScope scope, Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand($"SELECT job_type,payload,attempts,maximum_attempts,lease_owner,fencing_token,lease_expires FROM \"{schema}\".jobs WHERE tenant=@tenant AND queue=@queue AND id=@id AND status=1", connection);
        Add(command, "tenant", scope.Tenant); Add(command, "queue", scope.Queue); Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return new(id, scope, reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    internal static void Add<T>(BlueTuskCommand command, string name, T value) => command.Parameters.Add(new BlueTuskParameter<T>(value) { ParameterName = name });
}

internal sealed record PhysicalPayload(string Tenant, string Mode, int Number);
internal sealed record PhaseMeasurement(string Phase, double ElapsedMilliseconds, string WalPosition, long DatabaseBytes,
    double ProcessCpuMilliseconds, long WorkingSetBytes, long ManagedBytes, long TotalAllocatedBytes,
    int Gen0, int Gen1, int Gen2, int PoolTotal, int PoolBusy, int PoolWaiting);
internal sealed record RecoveredLease(string Product, string Tenant, Guid JobId, int Attempt, long BeforeFence, long AfterFence, bool StaleCompletionRejected, bool StaleEffectRejected);
internal sealed record WorkflowProof(string Tenant, string Scenario, Guid Id, string Status, int ProduceCalls, int UndoCalls, int FinishCalls, bool ReplayMatches);
internal sealed record OperationMeasurement(string Phase, string Name, int Observed, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds);
internal sealed record PhysicalRecoveryReport(string Fixture, string Server, string Image, string SourceDurability,
    string BeforeSystemIdentifier, string AfterSystemIdentifier, int BeforeTimeline, int AfterTimeline,
    int AcknowledgedJobs, int VerifiedJobs, int AcknowledgedBusinessAdmissions, int JobEffects, int WorkflowEffects,
    double PrimaryKillToPromotionMilliseconds, double PrimaryKillToFirstEffectReplayMilliseconds, double PrimaryKillToFirstRecoveredMilliseconds, double PrimaryKillToDrainedMilliseconds,
    bool UnchangedMultihostSource, bool NoCrossTenantReads, bool UnavailableHealthDuringOutage, bool HealthyAfterRecovery,
    IReadOnlyList<RecoveredLease> Leases, IReadOnlyList<WorkflowProof> Workflows, IReadOnlyList<PhaseMeasurement> Measurements,
    IReadOnlyList<OperationMeasurement> Operations, string RecoveryConfiguration,
    string Qualification);

[JsonSerializable(typeof(PhysicalPayload))]
[JsonSerializable(typeof(PhysicalRecoveryReport))]
[JsonSerializable(typeof(OperationMeasurement[]))]
internal sealed partial class PhysicalRecoveryJsonContext : JsonSerializerContext;
