using System.Diagnostics;
using System.Reflection;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    private static readonly JobScope FaultScope = new("tenant-fault", "fault");

    private static async Task<int> FaultWorkerAsync(string connectionString, string jobsSchema, string effectSchema)
    {
        Check(jobsSchema.StartsWith("fault_jobs_", StringComparison.Ordinal) && effectSchema.StartsWith("fault_effect_", StringComparison.Ordinal)
            && jobsSchema.Length == 43 && effectSchema.Length == 45
            && jobsSchema[11..].All(char.IsAsciiHexDigit) && effectSchema[13..].All(char.IsAsciiHexDigit), "child schema identity");
        await using var source = Source(connectionString, 4);
        var jobs = new PostgreSqlJobStore(source, new JobStoreOptions { Schema = jobsSchema });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var registry = new JobHandlerRegistry().Register("fault.v1", HarnessJsonContext.Default.LoadPayload, async (payload, context, token) =>
        {
            var result = await jobs.ExecuteFencedAsync(context.Lease!, async (connection, transaction, cancellation) =>
            {
                await InsertEffectAsync(connection, transaction, effectSchema, context.JobId, context.Scope.Tenant, payload.Sequence, cancellation);
                return true;
            }, token);
            Check(result.Executed, "child committed effect");
            // The parent kills this actual process after observing the committed effect, before job acknowledgement.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var worker = new JobWorker(jobs, FaultScope, "fault-child", registry, new JobWorkerOptions
        {
            Concurrency = 1,
            ClaimBatchSize = 1,
            LeaseDuration = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(10),
            DispatchRecurringSchedules = false,
        });
        await worker.RunAsync(timeout.Token);
        return 0;
    }

    private static async Task<FaultResult> ProcessDeathAsync(string connectionString)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string jobsSchema = "fault_jobs_" + suffix;
        string effectSchema = "fault_effect_" + suffix;
        await using var source = Source(connectionString, 8);
        var jobs = new PostgreSqlJobStore(source, new JobStoreOptions { Schema = jobsSchema });
        Process? child = null;
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            await jobs.InitializeAsync();
            await ExecuteAsync(source, $"CREATE SCHEMA \"{effectSchema}\"; CREATE TABLE \"{effectSchema}\".effects (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, sequence integer NOT NULL)");
            Guid id = await jobs.EnqueueAsync(JobRequest.FromJson(FaultScope, "fault.v1", new LoadPayload(0, 1, 0, ""), HarnessJsonContext.Default.LoadPayload));
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }

            start.ArgumentList.Add("--fault-worker");
            start.ArgumentList.Add(jobsSchema);
            start.ArgumentList.Add(effectSchema);
            child = Process.Start(start) ?? throw new InvalidOperationException("Fault process did not start.");
            output = child.StandardOutput.ReadToEndAsync();
            error = child.StandardError.ReadToEndAsync();
            await WaitUntilAsync(async () =>
            {
                Check(!child.HasExited, "fault child remains alive until killed");
                return await EffectCountAsync(source, effectSchema) == 1;
            }, TimeSpan.FromSeconds(20));
            var original = await jobs.ReadAsync(FaultScope, id);
            Check(original is { Status: JobStatus.Running, Attempts: 1 }, "child has live first attempt");
            var recovery = Stopwatch.StartNew();
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            _ = await output;
            _ = await error;
            JobLease? recovered = null;
            await WaitUntilAsync(async () =>
            {
                var claimed = await jobs.ClaimAsync(FaultScope, "fault-recovery", 1, TimeSpan.FromSeconds(10));
                recovered = claimed.SingleOrDefault();
                return recovered is not null;
            }, TimeSpan.FromSeconds(15));
            Check(recovered!.Attempt == 2 && recovered.FencingToken > original!.FencingToken, "recovery advanced attempt and fence");
            var result = await jobs.ExecuteFencedAsync(recovered, async (connection, transaction, cancellation) =>
            {
                await InsertEffectAsync(connection, transaction, effectSchema, id, FaultScope.Tenant, 1, cancellation);
                return true;
            });
            Check(result.Executed && await jobs.CompleteAsync(recovered), "recovered attempt commits");
            var stale = recovered with { Owner = "fault-child", FencingToken = original!.FencingToken, Attempt = 1 };
            Check(!await jobs.CompleteAsync(stale), "killed process capability is fenced");
            recovery.Stop();
            int effects = await EffectCountAsync(source, effectSchema);
            Check(effects == 1, "idempotent effect survives death before acknowledgement");
            return new("process-death-after-effect-before-ack", true, recovery.Elapsed.TotalMilliseconds, recovered.Attempt, effects, false);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }

                if (output is not null) { _ = await output; }
                if (error is not null) { _ = await error; }
                child.Dispose();
            }

            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS \"{effectSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{jobsSchema}\" CASCADE");
        }
    }

    private static async Task<FaultResult> AmbiguousCommitAsync(string connectionString)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string jobsSchema = "fault_jobs_" + suffix;
        string effectSchema = "fault_effect_" + suffix;
        await using var source = Source(connectionString, 8);
        var jobs = new PostgreSqlJobStore(source, new JobStoreOptions { Schema = jobsSchema });
        try
        {
            await jobs.InitializeAsync();
            await ExecuteAsync(source, $"CREATE SCHEMA \"{effectSchema}\"; CREATE TABLE \"{effectSchema}\".effects (id uuid PRIMARY KEY, tenant varchar(200) NOT NULL, sequence integer NOT NULL)");
            var settings = new BlueTuskConnectionStringBuilder(connectionString);
            await using var proxy = new CommitAcknowledgementProxy(settings.Host, settings.Port);
            settings.Host = "127.0.0.1";
            settings.Port = proxy.Port;
            settings.Pooling = true;
            settings.MaximumPoolSize = 1;
            settings.MinimumPoolSize = 0;
            settings.ApplicationName = ApplicationName;
            await using var proxySource = BlueTuskDataSource.Create(settings.ConnectionString);
            var request = JobRequest.FromJson(FaultScope, "fault.v1", new LoadPayload(0, 2, 0, ""), HarnessJsonContext.Default.LoadPayload)
                with
            { DeduplicationKey = "ambiguous-commit" };
            Guid id;
            bool clientFailed = false;
            await using (var connection = await proxySource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                id = await jobs.EnqueueAsync(request, transaction, CancellationToken.None);
                await InsertEffectAsync(connection, transaction, effectSchema, id, FaultScope.Tenant, 2, CancellationToken.None);
                try
                {
                    await transaction.CommitAsync();
                }
                catch (Exception exception) when (exception is BlueTuskException or IOException or InvalidOperationException)
                {
                    clientFailed = true;
                    Check(connection.State == System.Data.ConnectionState.Closed, "provider closes ambiguous connection before transaction disposal");
                }
            }

            Check(clientFailed && proxy.DroppedCommitAcknowledgement, "server commit acknowledgement was dropped");
            var recovery = Stopwatch.StartNew();
            await using (var replacement = await proxySource.OpenConnectionAsync())
            await using (var command = new BlueTuskCommand("SELECT 1::integer", replacement))
            {
                Check(await command.ExecuteScalarAsync(CancellationToken.None) is 1, "pool replaces the retired physical session");
            }

            Guid retried = await jobs.EnqueueAsync(request);
            var durable = await jobs.ReadAsync(FaultScope, id);
            int effects = await EffectCountAsync(source, effectSchema);
            Check(retried == id && durable is { Status: JobStatus.Pending } && effects == 1, "ambiguous commit resolved by durable deduplication");
            recovery.Stop();
            return new("ambiguous-commit-lost-server-ack", true, recovery.Elapsed.TotalMilliseconds, 0, effects, true);
        }
        finally
        {
            await ExecuteAsync(source, $"DROP SCHEMA IF EXISTS \"{effectSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{jobsSchema}\" CASCADE");
        }
    }

    private static async Task<int> EffectCountAsync(BlueTuskDataSource source, string schema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand($"SELECT count(*)::int4 FROM \"{schema}\".effects", connection);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
