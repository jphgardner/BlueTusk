using System.Globalization;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Documents;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Documents promises durable acknowledged saves, all-or-nothing multi-document sessions, tenant
/// isolation and revision compare-and-swap. Each disturbance interrupts a two-document save that
/// is blocked inside its open transaction, then proves every acknowledged document survived with
/// its exact value, the interrupted save is atomic, tenants stay isolated and a stale revision
/// cannot overwrite a newer one.
/// </summary>
internal static class DocumentsFailover
{
    private const string Application = "BlueTuskFailoverDocuments";
    private const string ChildApplication = "BlueTuskFailoverDocumentsChild";
    private const string Semantics = "acknowledged-save-durable; atomic-multi-document-save; revision-compare-and-swap";
    private const int Batches = 8;
    private const long BarrierKey = 180942166101;
    private static readonly TimeSpan Phase = TimeSpan.FromSeconds(180);
    private static readonly string[] Tenants = ["tenant-a", "tenant-b"];
    private static readonly DocumentCollectionDefinition<FailoverDocument> Collection = new("records", DocumentsJson.Default.FailoverDocument);

    internal static async Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token)
    {
        var results = new List<ScenarioResult>
        {
            await BackendTerminationAsync(fixture, token),
            await HostProcessKillAsync(fixture, token),
            await PrimaryCrashRestartAsync(fixture, token),
            // Promotion permanently removes the original primary, so it always runs last.
            await PromotionAsync(fixture, token),
        };
        return results;
    }

    private static async Task<ScenarioResult> BackendTerminationAsync(FailoverFixture fixture, CancellationToken token)
    {
        var recorder = new ScenarioRecorder("backend-termination", "pg_terminate_backend of the session holding the in-flight save", Semantics, Tenants.Length);
        await using var source = fixture.CreateSource(Application, 4);
        await using var store = await PrepareAsync(fixture, source, recorder, token);
        var acknowledged = await AcknowledgeAsync(store, token);
        recorder.Acknowledged = acknowledged.Count;
        await using var barrier = await fixture.HoldBarrierAsync(BarrierKey, token);
        var inFlight = SaveInFlightAsync(store, token);
        await fixture.WaitForAdvisoryWaitAsync(Application, Phase, token);
        recorder.MarkFault();
        recorder.Check(await fixture.TerminateAsync(Application, token) >= 1, "in-flight backend terminated");
        recorder.Check(await FailedAsync(inFlight), "interrupted save reported failure to its caller");
        await barrier.ReleaseAsync(token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, () => EnsurePostFaultAsync(store, token), Phase, "Documents save", token);
        await VerifyAsync(fixture, store, recorder, acknowledged, token);
        recorder.After = await fixture.IdentifyAsync(token);
        recorder.Check(recorder.After.SystemIdentifier == recorder.Before!.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline, "same server and timeline");
        return recorder.Complete();
    }

    private static async Task<ScenarioResult> HostProcessKillAsync(FailoverFixture fixture, CancellationToken token)
    {
        var recorder = new ScenarioRecorder("host-process-kill", "operating-system kill of the writer process while its save is blocked in an open transaction", Semantics, Tenants.Length);
        await using var source = fixture.CreateSource(Application, 4);
        await using var store = await PrepareAsync(fixture, source, recorder, token);
        await using var barrier = await fixture.HoldBarrierAsync(BarrierKey, token);
        await using var child = ChildProcess.Start("Documents", "writer", store.Options.Schema);
        for (int batch = 0; batch < Batches; batch++)
        {
            await child.ExpectAsync("ACK " + batch.ToString(CultureInfo.InvariantCulture), Phase, token);
        }

        await child.ExpectAsync("BLOCK_START", Phase, token);
        var acknowledged = Expected();
        recorder.Acknowledged = acknowledged.Count;
        await fixture.WaitForAdvisoryWaitAsync(ChildApplication, Phase, token);
        recorder.MarkFault();
        await child.KillAsync(token);
        recorder.Check(true, "writer process hard-killed while blocked");
        // The killed client cannot send COMMIT; once the owned barrier is released PostgreSQL reads EOF and rolls back.
        await barrier.ReleaseAsync(token);
        await fixture.WaitForNoSessionsAsync(ChildApplication, Phase, token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, () => EnsurePostFaultAsync(store, token), Phase, "Documents save", token);
        await VerifyAsync(fixture, store, recorder, acknowledged, token);
        recorder.After = await fixture.IdentifyAsync(token);
        recorder.Check(recorder.After.SystemIdentifier == recorder.Before!.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline, "same server and timeline");
        return recorder.Complete();
    }

    private static async Task<ScenarioResult> PrimaryCrashRestartAsync(FailoverFixture fixture, CancellationToken token)
    {
        var recorder = new ScenarioRecorder("primary-crash-restart", "SIGKILL of the PostgreSQL primary during an in-flight save, then restart of the same server", Semantics, Tenants.Length);
        await using var source = fixture.CreateSource(Application, 4);
        await using var store = await PrepareAsync(fixture, source, recorder, token);
        var acknowledged = await AcknowledgeAsync(store, token);
        recorder.Acknowledged = acknowledged.Count;
        await using var barrier = await fixture.HoldBarrierAsync(BarrierKey, token);
        var inFlight = SaveInFlightAsync(store, token);
        await fixture.WaitForAdvisoryWaitAsync(Application, Phase, token);
        recorder.MarkFault();
        await FailoverFixture.KillPrimaryAsync(token);
        recorder.Check(await FailedAsync(inFlight), "interrupted save reported failure to its caller");
        await FailoverFixture.StartPrimaryAsync(token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, () => EnsurePostFaultAsync(store, token), Phase, "Documents save", token);
        await fixture.RequireSynchronousAsync(Phase, token);
        recorder.Check(true, "synchronous standby reattached after restart");
        await VerifyAsync(fixture, store, recorder, acknowledged, token);
        recorder.After = await fixture.IdentifyAsync(token);
        recorder.Check(recorder.After.SystemIdentifier == recorder.Before!.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline, "same server and timeline");
        return recorder.Complete();
    }

    private static async Task<ScenarioResult> PromotionAsync(FailoverFixture fixture, CancellationToken token)
    {
        var recorder = new ScenarioRecorder("synchronous-standby-promotion", "SIGKILL of the PostgreSQL primary during an in-flight save, then promotion of the synchronous physical standby", Semantics, Tenants.Length);
        await using var source = fixture.CreateSource(Application, 4);
        await using var store = await PrepareAsync(fixture, source, recorder, token);
        var acknowledged = await AcknowledgeAsync(store, token);
        recorder.Acknowledged = acknowledged.Count;
        await using var barrier = await fixture.HoldBarrierAsync(BarrierKey, token);
        var inFlight = SaveInFlightAsync(store, token);
        await fixture.WaitForAdvisoryWaitAsync(Application, Phase, token);
        recorder.MarkFault();
        await FailoverFixture.KillPrimaryAsync(token);
        recorder.Check(await FailedAsync(inFlight), "interrupted save reported failure to its caller");
        await FailoverFixture.PromoteStandbyAsync(token);
        await fixture.ConfigurePromotedAsync(token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, () => EnsurePostFaultAsync(store, token), Phase, "Documents save", token);
        await VerifyAsync(fixture, store, recorder, acknowledged, token);
        recorder.After = await fixture.IdentifyAsync(token);
        recorder.Check(recorder.After.SystemIdentifier == recorder.Before!.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline + 1,
            "same system promoted to the next timeline");
        return recorder.Complete();
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "writer" && arguments is [var schema] && IsOwnedSchema(schema), "parent-generated Documents schema");
        await using var fixture = new FailoverFixture();
        await using var source = fixture.CreateSource(ChildApplication, 1);
        await using var store = new DocumentStore(source, new() { Schema = arguments[0], CommandsPerBatch = 1 });
        for (int batch = 0; batch < Batches; batch++)
        {
            await SaveBatchAsync(store, batch, token);
            Console.WriteLine("ACK " + batch.ToString(CultureInfo.InvariantCulture));
            await Console.Out.FlushAsync(token);
        }

        Console.WriteLine("BLOCK_START");
        await Console.Out.FlushAsync(token);
        await SaveInFlightAsync(store, token);
        throw new InvalidOperationException("The child unexpectedly completed its parent-blocked save.");
    }

    private static async Task<DocumentStore> PrepareAsync(FailoverFixture fixture, BlueTuskDataSource source, ScenarioRecorder recorder, CancellationToken token)
    {
        string schema = "fo_documents_" + Guid.NewGuid().ToString("N");
        await fixture.RequireSynchronousAsync(Phase, token);
        recorder.Check(true, "remote_apply synchronous standby before fault");
        recorder.Before = await fixture.IdentifyAsync(token);
        var store = new DocumentStore(source, new() { Schema = schema, CommandsPerBatch = 1 });
        await store.InitializeAsync(token);
        // The barrier blocks only the second document of the in-flight save, inside its open transaction.
        await FailoverFixture.ExecuteAsync(fixture.Admin, $"""
            CREATE FUNCTION "{schema}".failover_barrier() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN IF NEW.id='inflight-z' THEN PERFORM pg_advisory_xact_lock({BarrierKey.ToString(CultureInfo.InvariantCulture)}); END IF; RETURN NEW; END;$function$;
            CREATE TRIGGER failover_barrier BEFORE INSERT ON "{schema}".documents FOR EACH ROW EXECUTE FUNCTION "{schema}".failover_barrier();
            """, token);
        return store;
    }

    private static List<(string Tenant, string Id, int Batch)> Expected()
    {
        var expected = new List<(string, string, int)>();
        for (int batch = 0; batch < Batches; batch++)
        {
            for (int row = 0; row < 2; row++) { expected.Add((Tenant(batch), Id(batch, row), batch)); }
        }

        return expected;
    }

    private static async Task<List<(string Tenant, string Id, int Batch)>> AcknowledgeAsync(DocumentStore store, CancellationToken token)
    {
        for (int batch = 0; batch < Batches; batch++) { await SaveBatchAsync(store, batch, token); }
        return Expected();
    }

    private static async Task SaveBatchAsync(DocumentStore store, int batch, CancellationToken token)
    {
        using var session = store.OpenSession(Tenant(batch));
        for (int row = 0; row < 2; row++) { session.Insert(Collection, Id(batch, row), new(Tenant(batch), batch, row)); }
        FailoverFixture.Check((await session.SaveChangesAsync(token)).Count == 2, "acknowledged complete two-document save");
    }

    private static async Task SaveInFlightAsync(DocumentStore store, CancellationToken token)
    {
        using var session = store.OpenSession(Tenants[0]);
        session.Insert(Collection, "inflight-a", new(Tenants[0], -1, 0));
        session.Insert(Collection, "inflight-z", new(Tenants[0], -1, 1));
        _ = await session.SaveChangesAsync(token);
    }

    /// <summary>The documented retry pattern after an uncertain outcome: read, then insert only if absent.</summary>
    private static async Task EnsurePostFaultAsync(DocumentStore store, CancellationToken token)
    {
        if (await store.LoadAsync(Tenants[1], Collection, "post-fault", token) is not null) { return; }
        using var session = store.OpenSession(Tenants[1]);
        session.Insert(Collection, "post-fault", new(Tenants[1], -2, 0));
        _ = await session.SaveChangesAsync(token);
    }

    private static async Task VerifyAsync(FailoverFixture fixture, DocumentStore store, ScenarioRecorder recorder,
        List<(string Tenant, string Id, int Batch)> acknowledged, CancellationToken token)
    {
        int verified = 0;
        bool isolated = true;
        foreach (var (tenant, id, batch) in acknowledged)
        {
            var stored = await store.LoadAsync(tenant, Collection, id, token);
            if (stored is not null && stored.Value == new FailoverDocument(tenant, batch, id.EndsWith("-1", StringComparison.Ordinal) ? 1 : 0)) { verified++; }
            string other = tenant == Tenants[0] ? Tenants[1] : Tenants[0];
            isolated &= await store.LoadAsync(other, Collection, id, token) is null;
        }

        recorder.Verified = verified;
        recorder.Check(verified == acknowledged.Count, "every acknowledged document survived with its exact value");
        recorder.NoCrossTenantReads = isolated;
        recorder.Check(isolated, "no cross-tenant reads");
        bool first = await store.LoadAsync(Tenants[0], Collection, "inflight-a", token) is not null;
        bool second = await store.LoadAsync(Tenants[0], Collection, "inflight-z", token) is not null;
        recorder.InFlightAtomic = first == second;
        recorder.InFlightCommitted = first && second;
        recorder.Check(recorder.InFlightAtomic, "interrupted two-document save is all-or-nothing");
        recorder.ExpectedEffects = acknowledged.Count + 1 + (recorder.InFlightCommitted ? 2 : 0);
        recorder.ObservedEffects = await FailoverFixture.ScalarAsync<long>(fixture.Admin, $"SELECT count(*) FROM \"{store.Options.Schema}\".documents", token);
        recorder.Check(recorder.ObservedEffects == recorder.ExpectedEffects, "no lost, duplicated or partial documents");
        var current = (await store.LoadAsync(acknowledged[0].Tenant, Collection, acknowledged[0].Id, token))!;
        recorder.BeforeFence = current.Revision;
        using (var session = store.OpenSession(acknowledged[0].Tenant))
        {
            session.Replace(Collection, current.Id, current.Value with { Batch = 100 }, current.Revision);
            recorder.AfterFence = (await session.SaveChangesAsync(token))[0].Revision ?? 0;
        }

        recorder.Check(recorder.AfterFence > recorder.BeforeFence, "compare-and-swap advanced the revision");
        bool staleRejected = false;
        using (var stale = store.OpenSession(acknowledged[0].Tenant))
        {
            stale.Replace(Collection, current.Id, current.Value with { Batch = 999 }, current.Revision);
            try { _ = await stale.SaveChangesAsync(token); }
            catch (DocumentConcurrencyException) { staleRejected = true; }
        }

        recorder.StaleOwnerRejected = staleRejected;
        recorder.Check(staleRejected, "stale pre-fault revision rejected");
        recorder.Check((await store.LoadAsync(acknowledged[0].Tenant, Collection, current.Id, token))!.Value.Batch == 100, "stale writer changed nothing");
    }

    private static async Task<bool> FailedAsync(Task operation)
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

    private static bool IsOwnedSchema(string schema) => schema.Length == "fo_documents_".Length + 32 &&
        schema.StartsWith("fo_documents_", StringComparison.Ordinal) && schema["fo_documents_".Length..].All(char.IsAsciiHexDigitLower);

    private static string Tenant(int batch) => Tenants[batch % Tenants.Length];
    private static string Id(int batch, int row) => "ack-" + batch.ToString("D2", CultureInfo.InvariantCulture) + "-" + row.ToString(CultureInfo.InvariantCulture);
}

internal sealed record FailoverDocument(string Tenant, int Batch, int Row);

[JsonSerializable(typeof(FailoverDocument))]
internal sealed partial class DocumentsJson : JsonSerializerContext;
