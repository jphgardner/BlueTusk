using System.Globalization;
using BlueTusk.Data;
using BlueTusk.Search;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Search replaces a document's chunks atomically per version: a higher version replaces every
/// chunk in one transaction, an identical same-version replay is idempotent and a lower version is
/// ignored, so a stale writer cannot resurrect an older document (docs/search/README.md). Each
/// disturbance interrupts a version-2 replacement after its document fence and old-chunk deletion
/// are staged, while it inserts new chunks. Recovery must leave version 1 searchable whole, the
/// documented same-version retry must apply version 2 exactly once, a delayed version-1 writer
/// must be ignored, and every acknowledged document must stay searchable only in its tenant.
/// </summary>
internal sealed class SearchFailover : FailoverCase
{
    private const string Index = "library";
    private const int DocumentsPerTenant = 8;
    private static readonly string[] TenantIds = ["tenant-a", "tenant-b"];
    private readonly FailoverFixture _fixture;
    private readonly string _schema;
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlSearchStore _store;

    private SearchFailover(FailoverFixture fixture, string application, string schema)
    {
        _fixture = fixture;
        _schema = schema;
        _source = fixture.CreateSource(application, 4);
        _store = new PostgreSqlSearchStore(_source, new() { Schema = schema });
    }

    internal static Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
        ScenarioDriver.RunAllAsync(fixture, () => new SearchFailover(fixture, ScenarioDriver.Application, "fo_search_" + Guid.NewGuid().ToString("N")[..24]),
            [FaultKind.BackendTermination, FaultKind.HostProcessKill, FaultKind.PrimaryCrashRestart, FaultKind.StandbyPromotion], token);

    internal override string Family => "Search";
    internal override string Semantics => "acknowledged-version-durable; atomic-version-replacement; idempotent-same-version-replay; stale-version-ignored";
    internal override int Tenants => TenantIds.Length;
    internal override long BarrierKey => 180942166401;
    internal override string[] ChildArguments => [_schema];

    internal override async Task PrepareAsync(CancellationToken token)
    {
        await _store.InitializeAsync(token);
        // Blocks only the version-2 replacement of doc-00, after its fence and chunk deletion are staged.
        await FailoverFixture.ExecuteAsync(_fixture.Admin, $"""
            CREATE FUNCTION "{_schema}".failover_barrier() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN PERFORM pg_advisory_xact_lock({BarrierKey.ToString(CultureInfo.InvariantCulture)}); RETURN NEW; END;$function$;
            CREATE TRIGGER failover_barrier BEFORE INSERT ON "{_schema}".chunks FOR EACH ROW
                WHEN (NEW.document_id = 'doc-00' AND NEW.source_version = 2) EXECUTE FUNCTION "{_schema}".failover_barrier();
            """, token);
    }

    internal override async Task<int> AcknowledgeAsync(CancellationToken token)
    {
        int acknowledged = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int index = 0; index < DocumentsPerTenant; index++)
            {
                FailoverFixture.Check((await _store.UpsertAsync(Document(tenant, index, 1), token)).Status == SearchIngestionStatus.Applied, "a new acknowledged version");
                acknowledged++;
            }
        }

        return acknowledged;
    }

    internal override Task StartInFlightAsync(CancellationToken token) => _store.UpsertAsync(Document(0, 0, 2), token).AsTask();

    internal override Task<int> ReadChildAcknowledgementsAsync(ChildProcess child, CancellationToken token) =>
        ScenarioDriver.ReadAcknowledgementsAsync(child, DocumentsPerTenant * TenantIds.Length, token);

    internal override async Task CheckRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        var hits = (await _store.SearchAsync(new SearchScope(TenantIds[0], Index), new SearchRequest { Text = Token(0, 0) }, token)).Hits;
        bool atomic = hits.Count > 0 && hits.All(static hit => hit.DocumentId == "doc-00" && hit.SourceVersion == 1) && await MixedChunksAsync(token) == 0 &&
            await VersionAsync(0, 0, token) == 1;
        recorder.InFlightAtomic = atomic;
        recorder.Check(atomic, "interrupted replacement rolled back whole; version 1 stays searchable");
    }

    /// <summary>The documented retry after an uncertain outcome: replay the same version.</summary>
    internal override async Task RecoverAsync(CancellationToken token) =>
        FailoverFixture.Check((await _store.UpsertAsync(Document(0, 0, 2), token)).Status is SearchIngestionStatus.Applied or SearchIngestionStatus.AlreadyApplied,
            "same-version retry applied once");

    internal override async Task VerifyAsync(ScenarioRecorder recorder, CancellationToken token)
    {
        recorder.Check((await _store.UpsertAsync(Document(0, 0, 2), token)).Status == SearchIngestionStatus.AlreadyApplied, "repeated same-version replay is idempotent");
        recorder.BeforeFence = 1;
        recorder.AfterFence = await VersionAsync(0, 0, token);
        recorder.Check(recorder.AfterFence == 2, "the retried version is current");
        var stale = await _store.UpsertAsync(Document(0, 0, 1), token);
        recorder.StaleOwnerRejected = stale.Status == SearchIngestionStatus.StaleIgnored && stale.CurrentVersion == 2;
        recorder.Check(recorder.StaleOwnerRejected, "delayed version-1 writer ignored");
        int verified = 0;
        bool isolated = true;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int index = 0; index < DocumentsPerTenant; index++)
            {
                long expected = tenant == 0 && index == 0 ? 2 : 1;
                var hits = (await _store.SearchAsync(new SearchScope(TenantIds[tenant], Index), new SearchRequest { Text = Token(tenant, index) }, token)).Hits;
                if (hits.Count > 0 && hits.All(hit => hit.DocumentId == Id(index) && hit.SourceVersion == expected && hit.Content.Contains(Token(tenant, index), StringComparison.Ordinal)))
                {
                    verified++;
                }

                isolated &= (await _store.SearchAsync(new SearchScope(TenantIds[1 - tenant], Index), new SearchRequest { Text = Token(tenant, index) }, token)).Hits.Count == 0;
            }
        }

        recorder.Verified = verified;
        recorder.Check(verified == DocumentsPerTenant * TenantIds.Length, "every acknowledged document is searchable at its exact version");
        recorder.NoCrossTenantReads = isolated;
        recorder.Check(isolated, "no cross-tenant reads");
        recorder.InFlightCommitted = recorder.AfterFence == 2;
        recorder.Check(await MixedChunksAsync(token) == 0, "no document mixes chunks from two versions");
        recorder.ExpectedEffects = DocumentsPerTenant * TenantIds.Length;
        recorder.ObservedEffects = await FailoverFixture.ScalarAsync<long>(_fixture.Admin, $"SELECT count(*) FROM \"{_schema}\".documents WHERE NOT deleted", token);
        recorder.Check(recorder.ObservedEffects == recorder.ExpectedEffects, "no lost, duplicated or partial documents");
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "workload" && arguments is [var schema] && schema.Length == "fo_search_".Length + 24 &&
            schema.StartsWith("fo_search_", StringComparison.Ordinal) && schema["fo_search_".Length..].All(char.IsAsciiHexDigitLower), "parent-generated Search schema");
        await using var fixture = new FailoverFixture();
        await using var workload = new SearchFailover(fixture, ScenarioDriver.ChildApplication, arguments[0]);
        int acknowledged = 0;
        for (int tenant = 0; tenant < TenantIds.Length; tenant++)
        {
            for (int index = 0; index < DocumentsPerTenant; index++)
            {
                FailoverFixture.Check((await workload._store.UpsertAsync(Document(tenant, index, 1), token)).Status == SearchIngestionStatus.Applied, "child acknowledged version");
                await ScenarioDriver.AcknowledgeToParentAsync(acknowledged++, token);
            }
        }

        await ScenarioDriver.BlockStartAsync(token);
        _ = await workload._store.UpsertAsync(Document(0, 0, 2), token);
        throw new InvalidOperationException("The child unexpectedly completed its parent-blocked replacement.");
    }

    private async Task<long> VersionAsync(int tenant, int index, CancellationToken token) =>
        await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT source_version FROM \"{_schema}\".documents WHERE tenant='{TenantIds[tenant]}' AND index_name='{Index}' AND document_id='{Id(index)}'", token);

    private async Task<long> MixedChunksAsync(CancellationToken token) =>
        await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
            $"SELECT count(*) FROM \"{_schema}\".chunks c JOIN \"{_schema}\".documents d USING (tenant, index_name, document_id) WHERE c.source_version <> d.source_version", token);

    private static SearchDocument Document(int tenant, int index, long version) =>
        new(TenantIds[tenant], Index, Id(index), version, "Failover " + Id(index),
            "Version " + version.ToString(CultureInfo.InvariantCulture) + " of " + Token(tenant, index) + " survives the disturbance.", isPublic: true);

    private static string Id(int index) => "doc-" + index.ToString("D2", CultureInfo.InvariantCulture);
    private static string Token(int tenant, int index) => "token" + tenant.ToString(CultureInfo.InvariantCulture) + "x" + index.ToString("D2", CultureInfo.InvariantCulture);

    public override async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _source.DisposeAsync();
    }
}
