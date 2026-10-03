using System.Globalization;

namespace BlueTusk.Ecosystem.FailoverHarness;

internal enum FaultKind
{
    BackendTermination,
    HostProcessKill,
    PrimaryCrashRestart,
    StandbyPromotion,
}

/// <summary>
/// One family's product workload for one disturbance. The driver owns the fault; the case owns
/// the acknowledged work, the in-flight operation blocked inside its open transaction, recovery
/// and the product-specific correctness assertions.
/// </summary>
internal abstract class FailoverCase : IAsyncDisposable
{
    internal abstract string Family { get; }
    internal abstract string Semantics { get; }
    internal abstract int Tenants { get; }
    internal abstract long BarrierKey { get; }

    /// <summary>Creates owned schemas and product state on the writable primary.</summary>
    internal abstract Task PrepareAsync(CancellationToken token);

    /// <summary>Performs acknowledged work in this process; returns the acknowledged operation count.</summary>
    internal abstract Task<int> AcknowledgeAsync(CancellationToken token);

    /// <summary>Starts work that blocks at the barrier inside its open transaction.</summary>
    internal abstract Task StartInFlightAsync(CancellationToken token);

    /// <summary>Arguments for the child process that acknowledges work, prints BLOCK_START and then blocks.</summary>
    internal abstract string[] ChildArguments { get; }

    /// <summary>Reads the child's acknowledgements; returns the acknowledged operation count.</summary>
    internal abstract Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token);

    /// <summary>Immediately after the fault: the interrupted work must have left no partial state.</summary>
    internal abstract Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token);

    /// <summary>One real product operation proving the service resumed; retried until it first succeeds.</summary>
    internal abstract Task RecoverAsync(CancellationToken token);

    /// <summary>Exact acknowledged-effect, in-flight, fence and tenant assertions after recovery.</summary>
    internal abstract Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token);

    public abstract ValueTask DisposeAsync();
}

internal static class ScenarioDriver
{
    internal const string Application = "BlueTuskFailoverWorkload";
    internal const string ChildApplication = "BlueTuskFailoverWorkloadChild";
    internal static readonly TimeSpan Phase = TimeSpan.FromSeconds(180);

    internal static string Name(FaultKind fault) => fault switch
    {
        FaultKind.BackendTermination => "backend-termination",
        FaultKind.HostProcessKill => "host-process-kill",
        FaultKind.PrimaryCrashRestart => "primary-crash-restart",
        _ => "synchronous-standby-promotion",
    };

    private static string Describe(FaultKind fault) => fault switch
    {
        FaultKind.BackendTermination => "pg_terminate_backend of the session holding the in-flight operation",
        FaultKind.HostProcessKill => "operating-system kill of the workload process while its operation is blocked in an open transaction",
        FaultKind.PrimaryCrashRestart => "SIGKILL of the PostgreSQL primary during an in-flight operation, then restart of the same server",
        _ => "SIGKILL of the PostgreSQL primary during an in-flight operation, then promotion of the synchronous physical standby",
    };

    /// <summary>Runs every disturbance in order; promotion permanently removes the primary, so it runs last.</summary>
    internal static async Task<IReadOnlyList<ScenarioResult>> RunAllAsync(FailoverFixture fixture, Func<FailoverCase> create,
        IReadOnlyList<FaultKind> faults, CancellationToken token)
    {
        var results = new List<ScenarioResult>();
        foreach (var fault in faults) { results.Add(await RunAsync(fixture, create, fault, token)); }
        return results;
    }

    private static async Task<ScenarioResult> RunAsync(FailoverFixture fixture, Func<FailoverCase> create, FaultKind fault, CancellationToken token)
    {
        await using var workload = create();
        var recorder = new ScenarioRecorder(Name(fault), Describe(fault), workload.Semantics, workload.Tenants);
        await fixture.RequireSynchronousAsync(Phase, token);
        recorder.Check(true, "remote_apply synchronous standby before fault");
        recorder.Before = await fixture.IdentifyAsync(token);
        await workload.PrepareAsync(token);
        await using var barrier = await fixture.HoldBarrierAsync(workload.BarrierKey, token);
        Task? inFlight = null;
        if (fault == FaultKind.HostProcessKill)
        {
            await using var child = ChildProcess.Start(workload.Family, "workload", workload.ChildArguments);
            recorder.Acknowledged = await workload.ReadChildAcknowledgementsAsync(child, token);
            await child.ExpectAsync("BLOCK_START", Phase, token);
            await fixture.WaitForAdvisoryWaitAsync(ChildApplication, Phase, token);
            recorder.MarkFault();
            await child.KillAsync(token);
            recorder.Check(true, "workload process hard-killed while blocked");
            // The dead client cannot send COMMIT; once the barrier is released PostgreSQL reads EOF and rolls back.
            await barrier.ReleaseAsync(token);
            await fixture.WaitForNoSessionsAsync(ChildApplication, Phase, token);
        }
        else
        {
            recorder.Acknowledged = await workload.AcknowledgeAsync(token);
            inFlight = workload.StartInFlightAsync(token);
            await fixture.WaitForAdvisoryWaitAsync(Application, Phase, token);
            recorder.MarkFault();
            switch (fault)
            {
                case FaultKind.BackendTermination:
                    recorder.Check(await fixture.TerminateWaitingAsync(Application, token) == 1, "in-flight backend terminated");
                    recorder.Check(await FailedAsync(inFlight), "interrupted operation reported failure to its caller");
                    await barrier.ReleaseAsync(token);
                    break;
                case FaultKind.PrimaryCrashRestart:
                    await FailoverFixture.KillPrimaryAsync(token);
                    recorder.Check(await FailedAsync(inFlight), "interrupted operation reported failure to its caller");
                    await FailoverFixture.StartPrimaryAsync(token);
                    break;
                default:
                    await FailoverFixture.KillPrimaryAsync(token);
                    recorder.Check(await FailedAsync(inFlight), "interrupted operation reported failure to its caller");
                    await FailoverFixture.PromoteStandbyAsync(token);
                    await fixture.ConfigurePromotedAsync(token);
                    break;
            }
        }

        await FailoverFixture.WaitUntilAsync(async () => await fixture.IdentifyAsync(token) is not null, Phase, "writable server reachable after the fault", token);
        await workload.CheckRolledBackAsync(recorder, token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, () => workload.RecoverAsync(token), Phase,
            workload.Family + " operation", token);
        if (fault == FaultKind.PrimaryCrashRestart)
        {
            await fixture.RequireSynchronousAsync(Phase, token);
            recorder.Check(true, "synchronous standby reattached after restart");
        }

        await workload.VerifyAsync(recorder, token);
        recorder.After = await fixture.IdentifyAsync(token);
        if (fault == FaultKind.StandbyPromotion)
        {
            recorder.Check(recorder.After.SystemIdentifier == recorder.Before.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline + 1,
                "same system promoted to the next timeline");
        }
        else
        {
            recorder.Check(recorder.After.SystemIdentifier == recorder.Before.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline,
                "same server and timeline");
        }

        return recorder.Complete();
    }

    internal static async Task<bool> FailedAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Phase);
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not HarnessCheckException)
        {
            return true;
        }
    }

    /// <summary>Child protocol: one "ACK n" line per acknowledged operation, then BLOCK_START.</summary>
    internal static async Task<int> ReadAcknowledgementsAsync(ChildProcess child, int expected, CancellationToken token)
    {
        for (int index = 0; index < expected; index++)
        {
            await child.ExpectAsync("ACK " + index.ToString(CultureInfo.InvariantCulture), Phase, token);
        }

        return expected;
    }

    internal static async Task AcknowledgeToParentAsync(int index, CancellationToken token)
    {
        Console.WriteLine("ACK " + index.ToString(CultureInfo.InvariantCulture));
        await Console.Out.FlushAsync(token);
    }

    internal static async Task BlockStartAsync(CancellationToken token)
    {
        Console.WriteLine("BLOCK_START");
        await Console.Out.FlushAsync(token);
    }
}
