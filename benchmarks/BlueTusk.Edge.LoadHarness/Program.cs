using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Edge.Http;
using BlueTusk.Edge.Server;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.LoadHarness;

internal static class Program
{
    private const int Clients = 8;
    private const int RecordsPerScope = 256;
    private const int PayloadBytes = 4096;
    private const int FeedTail = 128;
    private static readonly JsonSerializerOptions ReportReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ReportWriteOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 30 or > 3600 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535)
        { Console.Error.WriteLine("Usage: <seconds 30..3600> <http-port> <report-path> <workspace-path>."); return 2; }
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_EDGE_LOAD_CONNECTION_STRING");
        var candidate = Environment.GetEnvironmentVariable("BLUETUSK_EDGE_LOAD_COMMIT");
        var sourceHash = Environment.GetEnvironmentVariable("BLUETUSK_EDGE_LOAD_SOURCE_SHA256");
        var binaryHash = Environment.GetEnvironmentVariable("BLUETUSK_EDGE_LOAD_BINARY_SHA256");
        var browserHash = Environment.GetEnvironmentVariable("BLUETUSK_EDGE_LOAD_BROWSER_SHA256");
        var image = Environment.GetEnvironmentVariable("BLUETUSK_EDGE_LOAD_IMAGE");
        if (string.IsNullOrWhiteSpace(connection) || candidate?.Length != 40 || sourceHash?.Length != 64 ||
            binaryHash?.Length != 64 || browserHash?.Length != 64 || string.IsNullOrWhiteSpace(image))
        { Console.Error.WriteLine("A disposable fixture and exact candidate/binary provenance are required."); return 2; }
        var executableHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(
            typeof(Program).Assembly.Location).ConfigureAwait(false)));
        if (!executableHash.Equals(binaryHash, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Executing Edge harness binary differs from the captured candidate."); }
        var reportPath = Path.GetFullPath(args[2]);
        var runRoot = Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(runRoot);
        var workspace = Path.GetFullPath(args[3]);
        var browserScript = Path.Combine(runRoot, "browser.mjs");
        if (!File.Exists(browserScript)) { throw new InvalidOperationException("The browser capacity worker is missing."); }
        var assetManifest = Path.Combine(runRoot, "browser-assets.json");
        if (!File.Exists(assetManifest) ||
            !Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(assetManifest).ConfigureAwait(false)))
                .Equals(browserHash, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("The browser asset snapshot manifest differs from the candidate."); }
        var schema = "edge_load_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16];
        var started = DateTimeOffset.UtcNow;
        CapacityHost? host = null;
        var clients = new List<SqliteCapacityClient>();
        Process? browser = null;
        Task<string>? browserOutputTask = null, browserErrorTask = null;
        try
        {
            await using var source = BlueTuskDataSource.Create(connection);
            await using var server = new PostgreSqlEdgeServerStore(source, new EdgeServerOptions
            {
                Schema = schema,
                MaxRecordBytes = 8192,
                MaxRecordsPerScope = 512,
                MaxRecordBytesPerScope = 4 * 1024 * 1024,
                MaxReceiptsPerScope = 512,
                MaxReceiptBytesPerScope = 4 * 1024 * 1024,
                MaxChangesPerScope = 1024,
                MaxChangeBytesPerScope = 8 * 1024 * 1024
            });
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 900));
            var token = lifetime.Token;
            try
            {
                await server.InitializeAsync(token).ConfigureAwait(false);
                await using (var setup = source.CreateCommand($"CREATE TABLE \"{schema}\".business_effects(mutation_id uuid PRIMARY KEY,tenant text NOT NULL,document_id text NOT NULL)"))
                { _ = await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
                for (var index = 0; index < Clients; index++)
                {
                    var scope = Scope(index);
                    await server.ActivateScopeAsync(scope, token).ConfigureAwait(false);
                    await SeedAsync(server, scope, index, token).ConfigureAwait(false);
                }
                host = await CapacityHost.StartAsync(server, schema, port, token).ConfigureAwait(false);
                for (var index = 0; index < Clients - 1; index++)
                { clients.Add(await SqliteCapacityClient.OpenAsync(index, Path.Combine(runRoot, "sqlite"), host.Endpoint, PayloadBytes, token).ConfigureAwait(false)); }
                var readyPath = Path.Combine(runRoot, "browser.ready");
                var startPath = Path.Combine(runRoot, "browser.start");
                var browserPath = Path.Combine(runRoot, "browser.json");
                var checkpointPath = Path.Combine(runRoot, "browser.checkpoint");
                var browserProfile = Path.Combine(runRoot, "browser-profile");
                var child = new ProcessStartInfo("node")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workspace
                };
                child.ArgumentList.Add(browserScript);
                child.Environment["BLUETUSK_EDGE_LOAD_ENDPOINT"] = host.Endpoint.ToString();
                child.Environment["BLUETUSK_EDGE_LOAD_SECONDS"] = seconds.ToString(CultureInfo.InvariantCulture);
                child.Environment["BLUETUSK_EDGE_LOAD_PROFILE"] = browserProfile;
                child.Environment["BLUETUSK_EDGE_LOAD_READY"] = readyPath;
                child.Environment["BLUETUSK_EDGE_LOAD_START"] = startPath;
                child.Environment["BLUETUSK_EDGE_LOAD_REPORT"] = browserPath;
                child.Environment["BLUETUSK_EDGE_LOAD_CHECKPOINT"] = checkpointPath;
                child.Environment["BLUETUSK_EDGE_LOAD_ASSETS"] = Path.Combine(runRoot, "browser-assets");
                browser = Process.Start(child) ?? throw new InvalidOperationException("Could not start the browser capacity worker.");
                browserOutputTask = browser.StandardOutput.ReadToEndAsync();
                browserErrorTask = browser.StandardError.ReadToEndAsync();
                var readiness = Stopwatch.StartNew();
                while (!File.Exists(readyPath))
                {
                    if (browser.HasExited || readiness.Elapsed > TimeSpan.FromMinutes(2))
                    { throw new InvalidOperationException("The browser capacity worker did not become ready."); }
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                var startUtc = DateTimeOffset.UtcNow.AddSeconds(2);
                await File.WriteAllTextAsync(startPath, startUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                var wait = startUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) { await Task.Delay(wait, token).ConfigureAwait(false); }
                var measured = Stopwatch.StartNew();
                var samples = new List<PhysicalSample> { await SampleAsync(source, schema, server, 0, token).ConfigureAwait(false) };
                using var sampleStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var observer = ObserveAsync(source, schema, server, clients, checkpointPath, samples, measured, sampleStop.Token);
                var workers = clients.Select(client => client.RunAsync(measured, seconds, token)).ToArray();
                var restart = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds * .75), token).ConfigureAwait(false);
                    await host!.DisposeAsync().ConfigureAwait(false);
                    host = await CapacityHost.StartAsync(server, schema, port, token).ConfigureAwait(false);
                }, token);
                var workerCompletion = Task.WhenAll(workers);
                var browserCompletion = browser.WaitForExitAsync(token);
                var monitored = new List<Task> { workerCompletion, browserCompletion, restart, observer };
                while (!workerCompletion.IsCompleted || !browserCompletion.IsCompleted)
                {
                    var finished = await Task.WhenAny(monitored).ConfigureAwait(false);
                    await finished.ConfigureAwait(false);
                    if (finished == observer) { throw new InvalidOperationException("Physical Edge observer stopped before the clients."); }
                    if (finished == browserCompletion && browser.ExitCode != 0)
                    { throw new InvalidOperationException("The browser capacity worker failed; raw logs are retained."); }
                    monitored.Remove(finished);
                }
                var drainSeconds = Math.Max(0, measured.Elapsed.TotalSeconds - seconds);
                await File.WriteAllTextAsync(Path.Combine(runRoot, "browser.log"),
                    (await browserOutputTask.ConfigureAwait(false)) + (await browserErrorTask.ConfigureAwait(false)), token).ConfigureAwait(false);
                await restart.ConfigureAwait(false);
                await sampleStop.CancelAsync().ConfigureAwait(false);
                await observer.ConfigureAwait(false);
                var after = await SampleAsync(source, schema, server, measured.Elapsed.TotalSeconds, token).ConfigureAwait(false);
                var browserReport = JsonSerializer.Deserialize<ClientReport>(await File.ReadAllTextAsync(browserPath, token).ConfigureAwait(false),
                    ReportReadOptions)
                    ?? throw new InvalidOperationException("The browser capacity report is missing.");
                var all = new List<ClientReport>();
                foreach (var client in clients) { all.Add(await client.ReportAsync(server, token).ConfigureAwait(false)); }
                all.Add(browserReport);
                var effects = await BusinessEffectsAsync(source, schema, token).ConfigureAwait(false);
                var exactBusiness = effects.Total == all.Sum(static client => client.Acknowledged) &&
                    all.All(client => effects.ByTenant.TryGetValue(Tenant(client.Index), out var count) && count == client.Acknowledged);
                var feed = await ExactFeedAsync(source, server, schema, all, token).ConfigureAwait(false);
                var lagging = await VerifyLaggingReaderAsync(server, host!.Endpoint, token).ConfigureAwait(false);
                var report = new CapacityReport(2, candidate, sourceHash, binaryHash, browserHash, image,
                    await ServerVersionAsync(source, token).ConfigureAwait(false), "eight-scope-ordered-offline-http",
                    started, DateTimeOffset.UtcNow, seconds, measured.Elapsed.TotalSeconds, drainSeconds,
                    Clients * RecordsPerScope, all.ToArray(), effects.Total, exactBusiness, feed, true, lagging,
                    samples.ToArray(), after, false, exactBusiness && feed && lagging && all.Count == Clients &&
                    all.All(static client => client.LostResponseRecovered && client.ReclaimedRetryFenced && client.ExactFinalCache));
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ReportWriteOptions), token).ConfigureAwait(false);
                if (!report.Passed) { throw new InvalidOperationException("Edge capacity correctness verification failed."); }
                Console.WriteLine($"Edge capacity raw run passed: {effects.Total.ToString(CultureInfo.InvariantCulture)} exact business effects in {seconds.ToString(CultureInfo.InvariantCulture)} offered seconds.");
                return 0;
            }
            finally
            {
                if (host is not null) { await host.DisposeAsync().ConfigureAwait(false); }
                await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                _ = await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            var frames = new StackTrace(error, true).GetFrames()?.Take(8).Select(frame => new
            {
                Method = frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name,
                Line = frame.GetFileLineNumber()
            }).ToArray();
            var sqlite = error as SqliteException;
            await File.WriteAllTextAsync(Path.Combine(runRoot, "failure.json"), JsonSerializer.Serialize(new
            {
                Type = error.GetType().Name,
                SqliteErrorCode = sqlite?.SqliteErrorCode,
                SqliteExtendedErrorCode = sqlite?.SqliteExtendedErrorCode,
                Frames = frames,
                Stage = "edge-capacity-workload",
                FailedUtc = DateTimeOffset.UtcNow,
                ProductionQualified = false
            })).ConfigureAwait(false);
            Console.Error.WriteLine($"Edge capacity run failed ({error.GetType().Name}); bounded partial evidence is retained.");
            return 1;
        }
        finally
        {
            if (browser is not null)
            {
                if (!browser.HasExited) { browser.Kill(entireProcessTree: true); await browser.WaitForExitAsync().ConfigureAwait(false); }
                if (browserOutputTask is { IsCompletedSuccessfully: true } output &&
                    browserErrorTask is { IsCompletedSuccessfully: true } errors)
                {
                    await File.WriteAllTextAsync(Path.Combine(runRoot, "browser.log"),
                        (await output.ConfigureAwait(false)) + (await errors.ConfigureAwait(false))).ConfigureAwait(false);
                }
                browser.Dispose();
            }
            foreach (var client in clients) { client.Dispose(); }
        }
    }

    private static async Task SeedAsync(PostgreSqlEdgeServerStore server, EdgeScope scope, int index, CancellationToken token)
    {
        var stream = EdgeOrderedMutationId.NewStreamId();
        var random = new Random(2048 + index);
        for (var number = 1; number <= RecordsPerScope; number++)
        {
            var key = "doc-" + (number - 1).ToString("D3", CultureInfo.InvariantCulture);
            var mutation = new EdgeMutation(scope, EdgeOrderedMutationId.Create(stream, number), key, 0,
                EdgeMutationKind.Upsert, Payload(random, PayloadBytes));
            var outcome = await server.ApplyMutationAsync(mutation, token).ConfigureAwait(false);
            if (outcome.Kind != EdgeMutationOutcomeKind.Applied) { throw new InvalidOperationException("Seed mutation conflicted."); }
            await server.FinalizeMutationReceiptAsync(mutation, token).ConfigureAwait(false);
            if (number % 64 == 0)
            { _ = await server.AdvanceOrderedReceiptHorizonAsync(scope, mutation.Id, 64, token).ConfigureAwait(false); }
        }
    }

    private static byte[] Payload(Random random, int bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var chars = new char[bytes];
        chars[0] = '{'; chars[1] = '"'; chars[2] = 'v'; chars[3] = '"'; chars[4] = ':'; chars[5] = '"';
        for (var i = 6; i < bytes - 2; i++) { chars[i] = alphabet[random.Next(alphabet.Length)]; }
        chars[^2] = '"'; chars[^1] = '}';
        return Encoding.UTF8.GetBytes(chars);
    }

    private static async Task ObserveAsync(DbDataSource source, string schema, PostgreSqlEdgeServerStore server,
        IReadOnlyList<SqliteCapacityClient> clients, string browserCheckpoint, List<PhysicalSample> samples,
        Stopwatch clock, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (samples.Count >= 1024) { throw new InvalidOperationException("Physical Edge time series exceeded its bound."); }
                foreach (var client in clients)
                {
                    await client.SampleAsync(token).ConfigureAwait(false);
                    var floor = Math.Max(0, await client.CheckpointAsync(token).ConfigureAwait(false) - FeedTail);
                    _ = await server.PruneChangesAsync(client.Scope, floor, 256, token).ConfigureAwait(false);
                }
                if (await BrowserCheckpointFile.ReadAsync(browserCheckpoint, token).ConfigureAwait(false) is { } position)
                { _ = await server.PruneChangesAsync(Scope(7), Math.Max(0, position - FeedTail), 256, token).ConfigureAwait(false); }
                samples.Add(await SampleAsync(source, schema, server, clock.Elapsed.TotalSeconds, token).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task<PhysicalSample> SampleAsync(DbDataSource source, string schema,
        PostgreSqlEdgeServerStore server, double elapsed, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),'0/0')::bigint,
                   pg_database_size(current_database()),
                   coalesce((SELECT sum(pg_total_relation_size(c.oid)) FROM pg_class c
                       JOIN pg_namespace n ON n.oid=c.relnamespace
                       WHERE n.nspname='{schema}' AND c.relkind='r'),0)::bigint
            """;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) { throw new InvalidOperationException("Physical Edge sample is missing."); }
        var wal = reader.GetInt64(0); var database = reader.GetInt64(1); var owned = reader.GetInt64(2);
        long receipts = 0, receiptBytes = 0, changes = 0, changeBytes = 0;
        for (var index = 0; index < Clients; index++)
        {
            var health = await server.ReadHealthAsync(Scope(index), token).ConfigureAwait(false);
            receipts += health.ReceiptCount; receiptBytes += health.ReceiptBytes;
            changes += health.ChangeCount; changeBytes += health.ChangeBytes;
        }
        return new(elapsed, owned, database, wal, receipts, receiptBytes, changes, changeBytes);
    }

    private static async Task<(long Total, Dictionary<string, long> ByTenant)> BusinessEffectsAsync(DbDataSource source,
        string schema, CancellationToken token)
    {
        var tenants = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT tenant,count(*) FROM \"{schema}\".business_effects GROUP BY tenant";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) { tenants.Add(reader.GetString(0), reader.GetInt64(1)); }
        return (tenants.Values.Sum(), tenants);
    }

    private static async Task<bool> ExactFeedAsync(DbDataSource source, PostgreSqlEdgeServerStore server,
        string schema, IReadOnlyList<ClientReport> clients, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        foreach (var client in clients)
        {
            var scope = Scope(client.Index);
            var health = await server.ReadHealthAsync(scope, token).ConfigureAwait(false);
            if (health.HeadPosition != RecordsPerScope + client.Acknowledged || health.RecordCount != RecordsPerScope ||
                health.ReceiptCount != 0 || health.ReceiptBytes != 0 || health.HeadPosition != client.FinalCheckpoint ||
                health.ChangeCount != health.HeadPosition - health.ReplayFloor || client.FinalHorizon != client.FinalOrderedSequence)
            { return false; }
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT (SELECT count(*) FROM "{schema}".changes WHERE tenant=@tenant AND scope='orders' AND epoch=1),
                       (SELECT coalesce(min(position),0) FROM "{schema}".changes WHERE tenant=@tenant AND scope='orders' AND epoch=1),
                       (SELECT coalesce(max(position),0) FROM "{schema}".changes WHERE tenant=@tenant AND scope='orders' AND epoch=1),
                       (SELECT admitted FROM "{schema}".ordered_streams WHERE tenant=@tenant AND scope='orders' AND epoch=1 AND stream_id=@stream),
                       (SELECT horizon FROM "{schema}".ordered_streams WHERE tenant=@tenant AND scope='orders' AND epoch=1 AND stream_id=@stream)
                """;
            Add(command, "tenant", scope.Tenant); Add(command, "stream", client.OrderedStreamId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) { return false; }
            var count = reader.GetInt64(0); var first = reader.GetInt64(1); var last = reader.GetInt64(2);
            if (count != health.ChangeCount || (count > 0 && (first != health.ReplayFloor + 1 || last != health.HeadPosition)) ||
                reader.IsDBNull(3) || reader.GetInt64(3) != client.FinalOrderedSequence ||
                reader.IsDBNull(4) || reader.GetInt64(4) != client.FinalHorizon)
            { return false; }
        }
        return true;
    }

    private static async Task<bool> VerifyLaggingReaderAsync(PostgreSqlEdgeServerStore server, Uri endpoint, CancellationToken token)
    {
        var scope = Scope(0);
        if ((await server.ReadHealthAsync(scope, token).ConfigureAwait(false)).ReplayFloor <= 0) { return false; }
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "edge-capacity-" + scope.Tenant);
        using var remote = new HttpEdgeRemoteTransport(http, endpoint);
        var expired = false;
        try { _ = await remote.ReadChangesAsync(scope, 0, 512, token).ConfigureAwait(false); }
        catch (EdgeHttpTransportException error) when (error.StatusCode == 410) { expired = true; }
        if (!expired) { return false; }
        var snapshot = await remote.BeginSnapshotAsync(scope, token).ConfigureAwait(false);
        var count = 0;
        await foreach (var batch in remote.ReadSnapshotAsync(scope, snapshot, token).ConfigureAwait(false)) { count += batch.Count; }
        return count == RecordsPerScope && snapshot.Position == (await server.ReadHealthAsync(scope, token).ConfigureAwait(false)).HeadPosition;
    }

    private static async Task<string> ServerVersionAsync(DbDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT version()";
        return (string)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL version is missing."));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }

    private static string Tenant(int index) => "tenant-" + index.ToString("D2", CultureInfo.InvariantCulture);
    private static EdgeScope Scope(int index) => new(Tenant(index), "orders", 1);
}
