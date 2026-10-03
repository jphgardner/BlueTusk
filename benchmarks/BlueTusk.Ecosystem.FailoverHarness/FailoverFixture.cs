using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// The owned synchronous primary/physical-standby pair provisioned by eng/run-expansion-failover.ps1.
/// Every disturbance verifies the owner and fixture labels before it touches a container.
/// </summary>
internal sealed class FailoverFixture : IAsyncDisposable
{
    private const string Prefix = "BLUETUSK_FAILOVER_";
    private readonly BlueTuskDataSource _admin;

    internal FailoverFixture()
    {
        PrimaryConnection = Required("PRIMARY");
        StandbyConnection = Required("STANDBY");
        _admin = CreateSource("BlueTuskFailoverAdmin", 2);
    }

    internal string PrimaryConnection { get; }

    /// <summary>The synchronous standby's application name configured by the runner for this family's rehearsal.</summary>
    internal static string StandbyApplicationName => Required("STANDBY_NAME") is var name && name.All(static character => char.IsAsciiLetterLower(character) || character == '_')
        ? name : throw new InvalidOperationException("The standby application name is not a plain identifier.");
    internal string StandbyConnection { get; }
    internal static string Fixture => Required("FIXTURE");
    internal static string Image => Required("IMAGE");
    internal BlueTuskDataSource Admin => _admin;

    /// <summary>
    /// The public provider multihost route used by every product under test: both fixture endpoints,
    /// a read-write target and a one-second connect timeout, as in the Jobs promotion rehearsal.
    /// No product source is replaced or re-pointed during a disturbance.
    /// </summary>
    internal string MultihostConnection(string application, int poolSize)
    {
        var first = new BlueTuskConnectionStringBuilder(PrimaryConnection);
        var second = new BlueTuskConnectionStringBuilder(StandbyConnection);
        first.Host = first.Host + "," + second.Host;
        first.Ports = first.Port.ToString(CultureInfo.InvariantCulture) + "," + second.Port.ToString(CultureInfo.InvariantCulture);
        first.TargetSessionAttributes = BlueTuskTargetSessionAttributes.ReadWrite;
        first.Timeout = TimeSpan.FromSeconds(1);
        first.MaximumPoolSize = poolSize;
        first.MinimumPoolSize = 0;
        first.ApplicationName = application;
        return first.ConnectionString;
    }

    internal BlueTuskDataSource CreateSource(string application, int poolSize = 8) =>
        BlueTuskDataSource.Create(MultihostConnection(application, poolSize));

    internal static async Task KillPrimaryAsync(CancellationToken token)
    {
        string name = Required("PRIMARY_CONTAINER");
        await VerifyOwnedAsync(name, token);
        _ = await DockerAsync(["kill", "--signal", "KILL", name], token);
        Check((await DockerAsync(["inspect", name, "--format", "{{.State.Running}}"], token)).Trim() == "false", "the primary is actually stopped");
    }

    internal static async Task StartPrimaryAsync(CancellationToken token)
    {
        string name = Required("PRIMARY_CONTAINER");
        await VerifyOwnedAsync(name, token);
        _ = await DockerAsync(["start", name], token);
    }

    internal static async Task PromoteStandbyAsync(CancellationToken token)
    {
        string name = Required("STANDBY_CONTAINER");
        await VerifyOwnedAsync(name, token);
        // The killed primary stays stopped; no restart or rejoin of the old primary is attempted.
        Check((await DockerAsync(["inspect", Required("PRIMARY_CONTAINER"), "--format", "{{.State.Running}}"], token)).Trim() == "false",
            "the former primary is fenced (stopped) before promotion");
        _ = await DockerAsync(["exec", name, "gosu", "postgres", "pg_ctl", "-D", "/var/lib/postgresql/data/pgdata", "promote", "-w", "-t", "30"], token);
    }

    /// <summary>The promoted survivor has no replica; it keeps local synchronous commit.</summary>
    internal async Task ConfigurePromotedAsync(CancellationToken token)
    {
        await using var promoted = BlueTuskDataSource.Create(new BlueTuskConnectionStringBuilder(StandbyConnection) { ApplicationName = "BlueTuskFailoverAdmin" }.ConnectionString);
        await ExecuteAsync(promoted, "ALTER SYSTEM SET synchronous_standby_names=''", token);
        await ExecuteAsync(promoted, "ALTER SYSTEM SET synchronous_commit='on'", token);
        await ExecuteAsync(promoted, "SELECT pg_reload_conf()", token);
        await ExecuteAsync(promoted, "CHECKPOINT", token);
    }

    /// <summary>Requires remote_apply and a streaming synchronous standby before a disturbance.</summary>
    internal async Task RequireSynchronousAsync(TimeSpan deadline, CancellationToken token)
    {
        await WaitUntilAsync(async () =>
        {
            try
            {
                await using var primary = BlueTuskDataSource.Create(new BlueTuskConnectionStringBuilder(PrimaryConnection) { ApplicationName = "BlueTuskFailoverAdmin", Timeout = TimeSpan.FromSeconds(1) }.ConnectionString);
                return await ScalarAsync<bool>(primary,
                    "SELECT current_setting('synchronous_commit')='remote_apply' AND EXISTS(SELECT 1 FROM pg_stat_replication WHERE application_name='" + StandbyApplicationName + "' AND state='streaming' AND sync_state='sync')",
                    token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return false;
            }
        }, deadline, "remote_apply synchronous standby", token);
    }

    internal async Task<ServerIdentity> IdentifyAsync(CancellationToken token)
    {
        await using var connection = await _admin.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand("SELECT (SELECT system_identifier::text FROM pg_control_system()), (SELECT timeline_id FROM pg_control_checkpoint()), version(), pg_is_in_recovery()", connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        Check(await reader.ReadAsync(token), "server identity is readable");
        Check(!reader.GetBoolean(3), "the routed server is the writable primary");
        return new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
    }

    /// <summary>Holds a session advisory lock on the current writable server. Product work that reaches
    /// the matching transaction lock blocks inside its open transaction until released.</summary>
    internal async Task<Barrier> HoldBarrierAsync(long key, CancellationToken token)
    {
        var connection = await _admin.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand("SELECT pg_advisory_lock(" + key.ToString(CultureInfo.InvariantCulture) + ")", connection);
        _ = await command.ExecuteNonQueryAsync(token);
        return new(connection, key);
    }

    internal async Task WaitForAdvisoryWaitAsync(string application, TimeSpan deadline, CancellationToken token) =>
        await WaitUntilAsync(async () => await ScalarAsync<long>(_admin,
            "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='" + application +
            "' AND wait_event_type='Lock' AND lower(wait_event)='advisory'", token) == 1, deadline, "in-flight work blocked at its barrier", token);

    internal async Task WaitForIdleInTransactionAsync(string application, TimeSpan deadline, CancellationToken token) =>
        await WaitUntilAsync(async () => await ScalarAsync<long>(_admin,
            "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='" + application +
            "' AND state='idle in transaction'", token) >= 1, deadline, "in-flight work held open in its transaction", token);

    internal async Task<long> TerminateAsync(string application, CancellationToken token) =>
        await ScalarAsync<long>(_admin,
            "SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname=current_database() AND application_name='" + application + "' AND pid<>pg_backend_pid()", token);

    internal async Task<long> TerminateIdleInTransactionAsync(string application, CancellationToken token) =>
        await ScalarAsync<long>(_admin,
            "SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname=current_database() AND application_name='" + application + "' AND state='idle in transaction'", token);

    internal async Task WaitForNoSessionsAsync(string application, TimeSpan deadline, CancellationToken token) =>
        await WaitUntilAsync(async () => await ScalarAsync<long>(_admin,
            "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='" + application + "'", token) == 0,
            deadline, "disturbed sessions drained", token);

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

    /// <summary>Polls a condition, tolerating transient disturbance failures, until its bounded deadline.</summary>
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan deadline, string contract, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            bool satisfied;
            try { satisfied = await condition(); }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested) { satisfied = false; }
            if (satisfied) { return; }
            Check(Stopwatch.GetElapsedTime(started) < deadline, contract + " within its bounded deadline");
            await Task.Delay(50, token);
        }
    }

    /// <summary>Retries one real product operation until it first succeeds; returns the elapsed time since <paramref name="faultTimestamp"/>.</summary>
    internal static async Task<double> FirstSuccessAsync(long faultTimestamp, Func<Task> operation, TimeSpan deadline, string contract, CancellationToken token)
    {
        while (true)
        {
            try
            {
                await operation();
                return Stopwatch.GetElapsedTime(faultTimestamp).TotalMilliseconds;
            }
            catch (Exception exception) when (exception is not HarnessCheckException && (exception is not OperationCanceledException || !token.IsCancellationRequested))
            {
                Check(Stopwatch.GetElapsedTime(faultTimestamp) < deadline, contract + " resumed within its bounded deadline");
                await Task.Delay(100, token);
            }
        }
    }

    internal static void Check(bool valid, string contract)
    {
        if (!valid) { throw new HarnessCheckException("Failover invariant failed: " + contract); }
    }

    private static async Task VerifyOwnedAsync(string name, CancellationToken token)
    {
        string actual = (await DockerAsync(["inspect", name, "--format", "{{index .Config.Labels \"bluetusk.owner\"}}|{{index .Config.Labels \"bluetusk.fixture\"}}"], token)).Trim();
        Check(actual == Required("OWNER") + "|" + Fixture, "the disturbance targets only this labelled disposable fixture");
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
            // Docker diagnostics can contain fixture settings; never echo them.
            Check(process.ExitCode == 0, "owned Docker operation '" + arguments[0] + "' succeeded");
            return result;
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            try { await Task.WhenAll(stdout, stderr); } catch (InvalidOperationException) { } catch (OperationCanceledException) { }
            throw;
        }
    }

    internal static string Required(string suffix) => Environment.GetEnvironmentVariable(Prefix + suffix) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Run eng/run-expansion-failover.ps1; a real owned primary/standby fixture is required.");

    public async ValueTask DisposeAsync() => await _admin.DisposeAsync();

    internal sealed class Barrier(BlueTuskConnection connection, long key) : IAsyncDisposable
    {
        private bool _released;

        /// <summary>Releases the lock; a barrier lost with its crashed server is already released.</summary>
        internal async Task ReleaseAsync(CancellationToken token)
        {
            if (_released) { return; }
            _released = true;
            await using var command = new BlueTuskCommand("SELECT pg_advisory_unlock(" + key.ToString(CultureInfo.InvariantCulture) + ")", connection);
            Check(await command.ExecuteScalarAsync(token) is true, "the owned barrier was released");
        }

        public async ValueTask DisposeAsync()
        {
            _released = true;
            try { await connection.DisposeAsync(); }
            catch (Exception exception) when (exception is BlueTuskException or IOException or InvalidOperationException) { }
        }
    }
}

internal sealed record ServerIdentity(string SystemIdentifier, int Timeline, string Version);

internal sealed class HarnessCheckException(string message) : Exception(message);
