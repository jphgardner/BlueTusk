using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;

namespace BlueTusk.Search.LoadHarness;

internal sealed record StorageSample(double ElapsedSeconds, long OwnedRelationBytes, long DatabaseBytes, long WalInsertBytes);
internal sealed record IndexObservation(string Name, string Method, long Scans, long TuplesRead, long Bytes);
internal sealed record Latency(long Count, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds);
internal sealed record TenantProgress(int Tenant, long WritesOffered, long WritesAccepted, long WritesRejected,
    long WriteScheduleSkipped, long ReadsOffered, long ReadsAccepted, long ReadsRejected, long ReadScheduleSkipped,
    long SelectiveReadsOffered, long SelectiveReadsAccepted, long SelectiveReadsRejected, long SelectiveReadsEmpty);
internal sealed record CampaignReport(int FormatVersion, string CandidateSha, string SourceTreeSha256, string HarnessBinarySha256, string PostgreSqlImage,
    string PostgreSqlVersion, string OperatingSystem, string Runtime, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    string Workload, int DurationSeconds, int Tenants, int DocumentsPerTenant, int ContentBytes, int Writers, int Readers,
    int SeededDocuments, int WriteOfferIntervalMilliseconds, int ReadOfferIntervalMilliseconds,
    long WritesOffered, long WritesAccepted, long WritesRejected, long WriteScheduleSkipped,
    long ReadsOffered, long ReadsAccepted, long ReadsRejected, long ReadScheduleSkipped,
    long SelectiveReadsOffered, long SelectiveReadsAccepted, long SelectiveReadsRejected, long SelectiveReadsEmpty,
    long MaintenancePruneAttempts, long MaintenancePruneRejected, long MaintenancePruneRemoved,
    double MeasuredSeconds, double DrainSeconds, Latency WriteLatency, Latency ReadLatency,
    Latency BroadReadLatency, Latency SelectiveReadLatency, IndexObservation[] FullTextIndexes,
    Latency? RejectedWriteLatency, Latency? RejectedReadLatency,
    TenantProgress[] TenantProgress, StorageSample[] StorageSamples, StorageSample AfterDrain,
    long ExactVersionRows, bool StaleFenceRejected, bool SameVersionReplayAccepted, bool ConflictRejected,
    bool AuthorizedRetrievalVerified, bool QueryRetentionDrained, bool ProductionQualified, bool Passed);

[JsonSerializable(typeof(CampaignReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class CampaignJson : JsonSerializerContext;

internal sealed class LatencyCapture
{
    private readonly ConcurrentQueue<double> _values = new();
    private int _count;

    internal void Record(long began)
    {
        if (Interlocked.Increment(ref _count) > 2_000_000)
        {
            throw new InvalidOperationException("Bounded latency sample capacity was exceeded.");
        }

        _values.Enqueue(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
    }

    internal Latency Distribution()
    {
        var sorted = _values.ToArray();
        if (sorted.Length == 0 || sorted.Length != _count)
        {
            throw new InvalidOperationException("Successful latency samples are missing or incomplete.");
        }

        Array.Sort(sorted);
        static double At(double[] rows, double fraction) => rows[Math.Max(0, (int)Math.Ceiling(rows.Length * fraction) - 1)];
        return new(sorted.Length, At(sorted, .5), At(sorted, .95), At(sorted, .99), sorted[^1]);
    }

    internal Latency? DistributionOrNull() => _count == 0 ? null : Distribution();
}

internal static class Program
{
    private const int Tenants = 8;
    private const int Readers = 4;
    private const int WriteIntervalMilliseconds = 200;
    private const int ReadIntervalMilliseconds = 100;
    private const string Index = "mixed";
    private static readonly string[] Allowed = ["readers"];
    private static readonly string[] Denied = ["restricted"];

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[0], out var seconds) || seconds is < 5 or > 3600 ||
            !int.TryParse(args[1], out var documentsPerTenant) || documentsPerTenant is < 32 or > 1024 ||
            !int.TryParse(args[2], out var contentBytes) || contentBytes is < 1024 or > 65536)
        {
            Console.Error.WriteLine("Usage: <seconds 5..3600> <documents-per-tenant 32..1024> <content-bytes 1024..65536> <report-path>.");
            return 2;
        }

        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_LOAD_CONNECTION_STRING");
        var candidate = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_LOAD_COMMIT");
        var sourceHash = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_LOAD_SOURCE_SHA256");
        var binaryHash = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_LOAD_BINARY_SHA256");
        var image = Environment.GetEnvironmentVariable("BLUETUSK_SEARCH_LOAD_IMAGE");
        if (string.IsNullOrWhiteSpace(connectionString) || candidate?.Length != 40 || sourceHash?.Length != 64 ||
            binaryHash?.Length != 64 || string.IsNullOrWhiteSpace(image))
        {
            Console.Error.WriteLine("The disposable fixture connection and exact candidate provenance are required.");
            return 2;
        }

        var executingBinary = System.Reflection.Assembly.GetExecutingAssembly().Location;
        Check(string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executingBinary))), binaryHash,
            StringComparison.OrdinalIgnoreCase), "The executing harness binary differs from the captured candidate.");

        var reportPath = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var schema = "search_load_" + Guid.NewGuid().ToString("N")[..16];
        var options = new SearchStoreOptions
        {
            Schema = schema,
            QueryLifetime = TimeSpan.FromSeconds(10),
            MaxActiveQueriesPerScope = 128,
            MaxConcurrentIngestions = Tenants,
            MaxPageSize = 10,
            MaxCandidateCount = 64
        };
        var started = DateTimeOffset.UtcNow;
        try
        {
            await using var source = BlueTuskDataSource.Create(connectionString);
            await using var store = new PostgreSqlSearchStore(source, options);
            try
            {
                await store.InitializeAsync();
                var versions = new long[Tenants, documentsPerTenant];
                await Task.WhenAll(Enumerable.Range(0, Tenants).Select(async tenant =>
                {
                    for (var document = 0; document < documentsPerTenant; document++)
                    {
                        var result = await store.UpsertAsync(Document(tenant, document, 1, contentBytes));
                        Check(result.Status == SearchIngestionStatus.Applied, "Seed admission did not apply exactly once.");
                        versions[tenant, document] = 1;
                    }
                }));

                await AnalyzeSeededCorpusAsync(source, schema);
                var postSeed = await SampleAsync(source, schema, 0);
                var samples = new List<StorageSample> { postSeed };
                var writeLatency = new LatencyCapture();
                var readLatency = new LatencyCapture();
                var broadReadLatency = new LatencyCapture();
                var selectiveReadLatency = new LatencyCapture();
                var rejectedWriteLatency = new LatencyCapture();
                var rejectedReadLatency = new LatencyCapture();
                var writesOffered = new long[Tenants];
                var writesAccepted = new long[Tenants];
                var writesRejected = new long[Tenants];
                var writeScheduleSkipped = new long[Tenants];
                var readsOffered = new long[Tenants];
                var readsAccepted = new long[Tenants];
                var readsRejected = new long[Tenants];
                var readScheduleSkipped = new long[Tenants];
                var selectiveReadsOffered = new long[Tenants];
                var selectiveReadsAccepted = new long[Tenants];
                var selectiveReadsRejected = new long[Tenants];
                var selectiveReadsEmpty = new long[Tenants];
                long maintenancePruneAttempts = 0;
                long maintenancePruneRejected = 0;
                long maintenancePruneRemoved = 0;
                var clock = Stopwatch.StartNew();
                var sampler = Task.Run(async () =>
                {
                    while (clock.Elapsed.TotalSeconds < seconds)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        Interlocked.Increment(ref maintenancePruneAttempts);
                        try
                        {
                            Interlocked.Add(ref maintenancePruneRemoved, await store.PruneExpiredQueriesAsync(1000));
                        }
                        catch (SearchBackpressureException)
                        {
                            // Maintenance uses the same bounded snapshot lock as reads. Record a
                            // transient admission rejection, then retry on the next sample interval.
                            Interlocked.Increment(ref maintenancePruneRejected);
                        }
                        samples.Add(await SampleAsync(source, schema, clock.Elapsed.TotalSeconds));
                        var progress = new
                        {
                            ElapsedSeconds = clock.Elapsed.TotalSeconds,
                            WritesAccepted = writesAccepted.Sum(),
                            WritesRejected = writesRejected.Sum(),
                            ReadsAccepted = readsAccepted.Sum(),
                            ReadsRejected = readsRejected.Sum(),
                            MaintenancePruneAttempts = Interlocked.Read(ref maintenancePruneAttempts),
                            MaintenancePruneRejected = Interlocked.Read(ref maintenancePruneRejected),
                            LatestStorageSample = samples[^1]
                        };
                        var progressPath = Path.ChangeExtension(reportPath, ".progress.json");
                        var temporaryPath = progressPath + ".tmp";
                        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(progress));
                        File.Move(temporaryPath, progressPath, overwrite: true);
                    }
                });
                var writers = Enumerable.Range(0, Tenants).Select(tenant => Task.Run(async () =>
                {
                    var ordinal = 0;
                    const double interval = WriteIntervalMilliseconds / 1000.0;
                    var plannedSlots = seconds * 1000 / WriteIntervalMilliseconds;
                    for (var nextSlot = 0; nextSlot < plannedSlots;)
                    {
                        var due = nextSlot * interval;
                        var wait = due - clock.Elapsed.TotalSeconds;
                        if (wait > 0) { await Task.Delay(TimeSpan.FromSeconds(wait)); }
                        if (clock.Elapsed.TotalSeconds >= seconds)
                        {
                            Interlocked.Add(ref writeScheduleSkipped[tenant], plannedSlots - nextSlot);
                            break;
                        }
                        var skipped = Math.Clamp((int)Math.Floor((clock.Elapsed.TotalSeconds - due) / interval), 0, plannedSlots - nextSlot - 1);
                        if (skipped > 0)
                        {
                            Interlocked.Add(ref writeScheduleSkipped[tenant], skipped);
                            nextSlot += skipped;
                        }
                        var document = ordinal++ % documentsPerTenant;
                        nextSlot++;
                        var nextVersion = versions[tenant, document] + 1;
                        Interlocked.Increment(ref writesOffered[tenant]);
                        var began = Stopwatch.GetTimestamp();
                        try
                        {
                            var result = await store.UpsertAsync(Document(tenant, document, nextVersion, contentBytes));
                            Check(result.Status == SearchIngestionStatus.Applied && result.CurrentVersion == nextVersion,
                                "Accepted write did not advance exactly one source version.");
                            versions[tenant, document] = nextVersion;
                            Interlocked.Increment(ref writesAccepted[tenant]);
                            writeLatency.Record(began);
                        }
                        catch (SearchBackpressureException)
                        {
                            Interlocked.Increment(ref writesRejected[tenant]);
                            rejectedWriteLatency.Record(began);
                        }
                    }
                })).ToArray();
                var readers = Enumerable.Range(0, Readers).Select(reader => Task.Run(async () =>
                {
                    var ordinal = 0;
                    const double interval = ReadIntervalMilliseconds / 1000.0;
                    var plannedSlots = seconds * 1000 / ReadIntervalMilliseconds;
                    for (var nextSlot = 0; nextSlot < plannedSlots;)
                    {
                        var due = nextSlot * interval;
                        var wait = due - clock.Elapsed.TotalSeconds;
                        if (wait > 0) { await Task.Delay(TimeSpan.FromSeconds(wait)); }
                        if (clock.Elapsed.TotalSeconds >= seconds)
                        {
                            for (var slot = nextSlot; slot < plannedSlots; slot++)
                            {
                                Interlocked.Increment(ref readScheduleSkipped[(reader + ordinal++ * Readers) % Tenants]);
                            }
                            break;
                        }
                        var skipped = Math.Clamp((int)Math.Floor((clock.Elapsed.TotalSeconds - due) / interval), 0, plannedSlots - nextSlot - 1);
                        if (skipped > 0)
                        {
                            for (var slot = 0; slot < skipped; slot++)
                            {
                                Interlocked.Increment(ref readScheduleSkipped[(reader + ordinal++ * Readers) % Tenants]);
                            }
                            nextSlot += skipped;
                        }
                        var queryOrdinal = ordinal++;
                        var tenant = (reader + queryOrdinal * Readers) % Tenants;
                        // Each reader alternates two tenants; include both in the selective quarter.
                        var selective = queryOrdinal % 8 < 2;
                        var document = (queryOrdinal / 8 % ((documentsPerTenant + 2) / 3)) * 3;
                        nextSlot++;
                        Interlocked.Increment(ref readsOffered[tenant]);
                        if (selective) { Interlocked.Increment(ref selectiveReadsOffered[tenant]); }
                        var began = Stopwatch.GetTimestamp();
                        try
                        {
                            var page = await store.SearchAsync(new SearchScope(Tenant(tenant), Index, Allowed),
                                new SearchRequest { Text = selective ? Marker(tenant, document) : "bluetusk", PageSize = 10, CandidateLimit = 64 });
                            if (selective)
                            {
                                Check(page.Hits.All(hit => hit.DocumentId == Id(tenant, document) &&
                                    hit.Content.Contains(Marker(tenant, document), StringComparison.Ordinal)),
                                    "Selective Search returned an unrelated or stale document.");
                                if (page.Hits.Count == 0) { Interlocked.Increment(ref selectiveReadsEmpty[tenant]); }
                            }
                            else { Check(page.Hits.Count > 0, "Broad authorized query returned no seeded documents."); }
                            foreach (var hit in page.Hits)
                            {
                                Check(hit.DocumentId.StartsWith(Tenant(tenant) + "-", StringComparison.Ordinal) &&
                                    !hit.DocumentId.Contains("-secret-", StringComparison.Ordinal),
                                    "Search returned a cross-tenant or unauthorized hit.");
                            }
                            Interlocked.Increment(ref readsAccepted[tenant]);
                            if (selective)
                            {
                                Interlocked.Increment(ref selectiveReadsAccepted[tenant]);
                                selectiveReadLatency.Record(began);
                            }
                            else { broadReadLatency.Record(began); }
                            readLatency.Record(began);
                        }
                        catch (SearchBackpressureException)
                        {
                            Interlocked.Increment(ref readsRejected[tenant]);
                            if (selective) { Interlocked.Increment(ref selectiveReadsRejected[tenant]); }
                            rejectedReadLatency.Record(began);
                        }
                    }
                })).ToArray();
                await Task.WhenAll(writers.Concat(readers).Append(sampler));
                var measuredSeconds = clock.Elapsed.TotalSeconds;
                var drain = Stopwatch.StartNew();
                var exactRows = await VerifyVersionsAsync(source, schema, versions, documentsPerTenant);
                var current = versions[0, 0];
                Check(current > 1, "No version advance occurred on the fence probe document.");
                var stale = await store.UpsertAsync(Document(0, 0, current - 1, contentBytes));
                var replay = await store.UpsertAsync(Document(0, 0, current, contentBytes));
                var conflictRejected = false;
                try
                {
                    var original = Document(0, 0, current, contentBytes);
                    _ = await store.UpsertAsync(new SearchDocument(original.Scope.Tenant, Index, original.Id,
                        current, original.Title, original.Content + "conflict", original.IsPublic));
                }
                catch (SearchVersionConflictException) { conflictRejected = true; }
                Check(stale.Status == SearchIngestionStatus.StaleIgnored &&
                    replay.Status == SearchIngestionStatus.AlreadyApplied && conflictRejected,
                    "Search version fences failed after concurrent writes.");
                _ = await VerifyVersionsAsync(source, schema, versions, documentsPerTenant);
                await Task.Delay(options.QueryLifetime + TimeSpan.FromSeconds(1));
                for (var pass = 0; pass < 4; pass++)
                {
                    if (await store.PruneExpiredQueriesAsync(1000) == 0) { break; }
                }
                var queriesDrained = await QueryCountAsync(source, schema) == 0;
                Check(queriesDrained, "Retained query snapshots did not drain.");
                var afterDrain = await SampleAsync(source, schema, clock.Elapsed.TotalSeconds);
                var fullTextIndexes = await ObserveFullTextIndexesAsync(source, schema);
                var progress = Enumerable.Range(0, Tenants).Select(tenant => new TenantProgress(tenant,
                    writesOffered[tenant], writesAccepted[tenant], writesRejected[tenant], writeScheduleSkipped[tenant],
                    readsOffered[tenant], readsAccepted[tenant], readsRejected[tenant], readScheduleSkipped[tenant],
                    selectiveReadsOffered[tenant], selectiveReadsAccepted[tenant], selectiveReadsRejected[tenant],
                    selectiveReadsEmpty[tenant])).ToArray();
                Check(writesOffered.Sum() + writeScheduleSkipped.Sum() == (long)Tenants * seconds * 1000 / WriteIntervalMilliseconds &&
                    readsOffered.Sum() + readScheduleSkipped.Sum() == (long)Readers * seconds * 1000 / ReadIntervalMilliseconds,
                    "The offered and skipped slots differ from the fixed workload schedule.");
                Check(progress.All(static item => item.WritesAccepted > 0 && item.ReadsAccepted > 0 &&
                    item.WritesOffered == item.WritesAccepted + item.WritesRejected &&
                    item.ReadsOffered == item.ReadsAccepted + item.ReadsRejected &&
                    item.SelectiveReadsOffered == item.SelectiveReadsAccepted + item.SelectiveReadsRejected &&
                    item.SelectiveReadsEmpty <= item.SelectiveReadsAccepted), "A tenant lost progress or admission accounting.");
                Check(fullTextIndexes.Length == 1, "The Search full-text index inventory is incomplete or ambiguous.");
                Check(string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executingBinary))), binaryHash,
                    StringComparison.OrdinalIgnoreCase), "The executing harness binary changed during the run.");
                var report = new CampaignReport(2, candidate, sourceHash, binaryHash, image, await ServerVersionAsync(source),
                    RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, started, DateTimeOffset.UtcNow,
                    "eight-tenant-seeded-analyzed-high-entropy-fulltext-broad-selective", seconds, Tenants, documentsPerTenant, contentBytes,
                    Tenants, Readers, Tenants * documentsPerTenant, WriteIntervalMilliseconds, ReadIntervalMilliseconds,
                    progress.Sum(static item => item.WritesOffered), progress.Sum(static item => item.WritesAccepted),
                    progress.Sum(static item => item.WritesRejected), progress.Sum(static item => item.WriteScheduleSkipped),
                    progress.Sum(static item => item.ReadsOffered), progress.Sum(static item => item.ReadsAccepted),
                    progress.Sum(static item => item.ReadsRejected), progress.Sum(static item => item.ReadScheduleSkipped),
                    progress.Sum(static item => item.SelectiveReadsOffered), progress.Sum(static item => item.SelectiveReadsAccepted),
                    progress.Sum(static item => item.SelectiveReadsRejected), progress.Sum(static item => item.SelectiveReadsEmpty),
                    maintenancePruneAttempts, maintenancePruneRejected, maintenancePruneRemoved,
                    measuredSeconds, drain.Elapsed.TotalSeconds, writeLatency.Distribution(), readLatency.Distribution(),
                    broadReadLatency.Distribution(), selectiveReadLatency.Distribution(), fullTextIndexes,
                    rejectedWriteLatency.DistributionOrNull(), rejectedReadLatency.DistributionOrNull(),
                    progress, samples.ToArray(), afterDrain, exactRows, true, true, true, true, queriesDrained, false, true);
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, CampaignJson.Default.CampaignReport));
                Console.WriteLine($"Search mixed workload completed. Raw report: {reportPath}");
            }
            finally
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
                _ = await command.ExecuteNonQueryAsync();
            }

            return 0;
        }
        catch (Exception error)
        {
            var failure = new
            {
                Passed = false,
                ProductionQualified = false,
                FailedUtc = DateTimeOffset.UtcNow,
                FailureType = error.GetType().Name,
                FailureStack = error.StackTrace,
                CandidateSha = candidate,
                SourceTreeSha256 = sourceHash
            };
            await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, ".failure.json"), JsonSerializer.Serialize(failure));
            Console.Error.WriteLine($"Search mixed workload failed ({error.GetType().Name}); no connection string or content is logged.");
            return 1;
        }
    }

    private static string Tenant(int tenant) => $"tenant-{tenant:D2}";
    private static string Marker(int tenant, int document) => $"docmarker{tenant:D2}x{document:D4}";
    private static string Id(int tenant, int document)
    {
        var visibility = (document % 3) switch { 0 => "public", 1 => "allowed", _ => "secret" };
        return $"{Tenant(tenant)}-{visibility}-{document:D4}";
    }

    private static SearchDocument Document(int tenant, int document, long version, int contentBytes)
    {
        var bytes = new byte[(contentBytes * 3 + 3) / 4];
        new Random(unchecked(19_373 + tenant * 1_000_003 + document * 7_919 + (int)version * 13_337)).NextBytes(bytes);
        var prefix = $"bluetusk {Tenant(tenant)} {Marker(tenant, document)} ";
        var content = prefix + Convert.ToBase64String(bytes)[..(contentBytes - prefix.Length)];
        var visibility = document % 3;
        return new SearchDocument(Tenant(tenant), Index, Id(tenant, document), version, "bluetusk reference",
            content, visibility == 0, visibility == 1 ? Allowed : visibility == 2 ? Denied : null);
    }

    private static async Task<StorageSample> SampleAsync(DbDataSource source, string schema, double elapsed)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),'0/0')::bigint,
                   pg_database_size(current_database()),
                   coalesce((SELECT sum(pg_total_relation_size(c.oid)) FROM pg_class c
                       JOIN pg_namespace n ON n.oid=c.relnamespace
                       WHERE n.nspname='{schema}' AND c.relkind='r'),0)::bigint
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Check(await reader.ReadAsync(), "Physical Search observation is missing.");
        return new(elapsed, reader.GetInt64(2), reader.GetInt64(1), reader.GetInt64(0));
    }

    private static async Task AnalyzeSeededCorpusAsync(DbDataSource source, string schema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ANALYZE \"{schema}\".documents";
        _ = await command.ExecuteNonQueryAsync();
        command.CommandText = $"ANALYZE \"{schema}\".chunks";
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<IndexObservation[]> ObserveFullTextIndexesAsync(DbDataSource source, string schema)
    {
        var indexes = new List<IndexObservation>();
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.indexrelname,a.amname,s.idx_scan,s.idx_tup_read,pg_relation_size(s.indexrelid)
            FROM pg_stat_user_indexes s
            JOIN pg_class i ON i.oid=s.indexrelid
            JOIN pg_am a ON a.oid=i.relam
            WHERE s.schemaname='{schema}' AND s.relname='chunks'
              AND s.indexrelname IN ('chunks_terms','chunks_terms_gist')
            ORDER BY s.indexrelname
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4)));
        }

        return indexes.ToArray();
    }

    private static async Task<string> ServerVersionAsync(DbDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> QueryCountAsync(DbDataSource source, string schema)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{schema}\".queries";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> VerifyVersionsAsync(DbDataSource source, string schema, long[,] versions, int documentsPerTenant)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT tenant,document_id,source_version,deleted FROM \"{schema}\".documents";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tenantName = reader.GetString(0);
            var id = reader.GetString(1);
            Check(!reader.GetBoolean(3) && id.StartsWith(tenantName + "-", StringComparison.Ordinal), "A Search row has wrong scope or deletion state.");
            var tenant = int.Parse(tenantName.AsSpan(7), CultureInfo.InvariantCulture);
            var document = int.Parse(id.AsSpan(id.Length - 4), CultureInfo.InvariantCulture);
            Check(tenant is >= 0 and < Tenants && document is >= 0 && document < documentsPerTenant &&
                id == Id(tenant, document) && reader.GetInt64(2) == versions[tenant, document] && found.Add(id),
                "Search source versions differ from accepted writer progress.");
        }
        Check(found.Count == Tenants * documentsPerTenant, "Search final document cardinality differs from the seeded fixed set.");
        return found.Count;
    }
}
