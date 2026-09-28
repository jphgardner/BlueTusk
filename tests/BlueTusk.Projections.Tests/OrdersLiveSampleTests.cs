using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BlueTusk.Replication;

namespace BlueTusk.Projections.Tests;

public sealed class OrdersLiveSampleTests
{
    [Fact]
    public async Task DeployedSampleBootstrapsRealWalSseReconnectProcessRestartAndControlledDdlRecoveryFromColocatedCheckpoint()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var publication = "proj_sample_pub_" + Guid.NewGuid().ToString("N");
        var slot = "proj_sample_slot_" + Guid.NewGuid().ToString("N");
        var rebuildSlot = "proj_sample_rebuild_" + Guid.NewGuid().ToString("N");
        var recoverySlot = "proj_sample_recovery_" + Guid.NewGuid().ToString("N");
        await SqlAsync(db, $"""
            CREATE TABLE "{db.Schema}".customers(id text NOT NULL, tenant text NOT NULL, name text NOT NULL, PRIMARY KEY(tenant,id));
            CREATE TABLE "{db.Schema}".orders(id text NOT NULL, tenant text NOT NULL, customer text NOT NULL, amount text NOT NULL, PRIMARY KEY(tenant,id));
            ALTER TABLE "{db.Schema}".customers REPLICA IDENTITY FULL;
            ALTER TABLE "{db.Schema}".orders REPLICA IDENTITY FULL;
            INSERT INTO "{db.Schema}".customers VALUES('customer','first','Alice');
            INSERT INTO "{db.Schema}".orders VALUES('1','first','customer','10');
            CREATE PUBLICATION "{publication}" FOR TABLE "{db.Schema}".customers, "{db.Schema}".orders;
            """, token);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = Timeout.InfiniteTimeSpan };
        const string apiKey = "sample-test-api-key-0123456789abcdefghijkl";
        http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        Process? process = null;
        Process? rebuildProcess = null;
        try
        {
            process = Start(db, publication, slot, port, apiKey);
            await ReadyAsync(http, process, token);
            using (var unauthorized = new HttpClient())
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await unauthorized.GetAsync(http.BaseAddress + "sample/status", token)).StatusCode);
            }
            string resume;
            await using (var sse = await ConnectAsync(http, null, token))
            {
                var initial = await sse.NextAsync(token);
                Assert.Equal("InitialResult", initial.GetProperty("event").GetProperty("kind").GetString());
                Assert.Equal("Alice", initial.GetProperty("event").GetProperty("rows")[0].GetProperty("Value").GetProperty("CustomerName").GetString());
                await SqlAsync(db, $"""
                    BEGIN; UPDATE "{db.Schema}".customers SET name='Bob' WHERE tenant='first' AND id='customer';
                    INSERT INTO "{db.Schema}".orders VALUES('2','first','customer','20'),('private','another','customer','999'); COMMIT;
                    """, token);
                var kinds = new HashSet<string>(StringComparer.Ordinal);
                JsonElement last = default;
                while (!kinds.Contains("RowUpdated") || !kinds.Contains("RowAdded"))
                {
                    last = await sse.NextAsync(token);
                    var body = last.GetProperty("event");
                    kinds.Add(body.GetProperty("kind").GetString()!);
                    if (body.TryGetProperty("row", out var row) && row.ValueKind != JsonValueKind.Null)
                    {
                        Assert.NotEqual("private", row.GetProperty("Key").GetString());
                    }
                }
                await SqlAsync(db, $"DELETE FROM \"{db.Schema}\".orders WHERE tenant='first' AND id='2'", token);
                do { last = await sse.NextAsync(token); } while (last.GetProperty("event").GetProperty("kind").GetString() != "RowRemoved");
                resume = last.GetProperty("resumeToken").GetString()!;
            }
            await SqlAsync(db, $"INSERT INTO \"{db.Schema}\".orders VALUES('3','first','customer','30')", token);
            await using (var resumed = await ConnectAsync(http, resume, token))
            {
                var added = await resumed.NextAsync(token);
                Assert.Equal("RowAdded", added.GetProperty("event").GetProperty("kind").GetString());
                resume = added.GetProperty("resumeToken").GetString()!;
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
            process.Dispose();
            process = null;
            // Simulate expiry after abrupt death immediately in this isolated test schema.
            await SqlAsync(db, $"UPDATE \"{db.Schema}\".state SET expires_at=clock_timestamp()-interval '1 second'; UPDATE \"{db.Schema}\".projection_live_replay SET expires_at=clock_timestamp()-interval '1 second'; UPDATE \"{db.Schema}\".customers SET name='Cara' WHERE tenant='first'; DELETE FROM \"{db.Schema}\".orders WHERE tenant='first' AND id='3'", token);
            process = Start(db, publication, slot, port, apiKey);
            await ReadyAsync(http, process, token);
            await using var restarted = await ConnectAsync(http, resume, token);
            JsonElement reset;
            do { reset = await restarted.NextAsync(token); } while (reset.GetProperty("event").GetProperty("kind").GetString() != "ResultReset");
            Assert.Equal("ServerRestart", reset.GetProperty("event").GetProperty("resetReason").GetString());
            var rows = reset.GetProperty("event").GetProperty("rows");
            if (rows.GetArrayLength() != 1 || rows[0].GetProperty("Value").GetProperty("CustomerName").GetString() != "Cara")
            {
                // Resume initialization may race the catch-up worker; every later materialized
                // revision remains pending and arrives through existing Live refresh/fan-out.
                var sawCara = false;
                var sawDelete = false;
                while (!sawCara || !sawDelete)
                {
                    var next = (await restarted.NextAsync(token)).GetProperty("event");
                    sawDelete |= next.GetProperty("kind").GetString() == "RowRemoved";
                    sawCara |= next.TryGetProperty("row", out var row) && row.ValueKind != JsonValueKind.Null && row.GetProperty("Value").GetProperty("CustomerName").GetString() == "Cara";
                }
            }
            Assert.Equal(10m, (await db.Store.ReadActiveAggregateAsync("orders", "first", "all", "total", token)).Value);
            listener.Start();
            var rebuildPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var candidateHttp = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + rebuildPort), Timeout = Timeout.InfiniteTimeSpan };
            candidateHttp.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            rebuildProcess = Start(db, publication, rebuildSlot, rebuildPort, apiKey, 2);
            await ReadyAsync(candidateHttp, rebuildProcess, token);
            using var promote = await candidateHttp.PostAsync("/sample/promote", new StringContent(
                "{\"requiredPosition\":0,\"expectedActiveVersion\":1,\"allowEquivalentSourceLineage\":true}", Encoding.UTF8, "application/json"), token);
            Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);
            JsonElement cutover;
            do { cutover = await restarted.NextAsync(token); }
            while (cutover.GetProperty("event").GetProperty("kind").GetString() != "ResultReset");
            Assert.Equal("SchemaChanged", cutover.GetProperty("event").GetProperty("resetReason").GetString());
            Assert.Equal(2, cutover.GetProperty("event").GetProperty("rows")[0].GetProperty("PublishedVersion").GetInt32());
            var cutoverResume = cutover.GetProperty("resumeToken").GetString()!;
            // The second HTTP node cannot publish this same authorized query while its durable
            // owner is live. After abrupt owner death it resumes the identical retained sequence.
            using (var unavailable = await candidateHttp.PostAsync("/bluetusk/live/sse", new StringContent("{\"query\":\"orders\",\"parameters\":{\"minimumAmount\":0}}", Encoding.UTF8, "application/json"), token))
            { Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode); }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
            process.Dispose(); process = null;
            await SqlAsync(db, $"UPDATE \"{db.Schema}\".projection_live_replay SET expires_at=clock_timestamp()-interval '1 second'", token);
            await using var replacementPublisher = await ConnectAsync(candidateHttp, cutoverResume, token);
            var takeover = await replacementPublisher.NextAsync(token);
            Assert.Equal("ServerRestart", takeover.GetProperty("event").GetProperty("resetReason").GetString());
            Assert.True(takeover.GetProperty("event").GetProperty("sequence").GetInt64() > cutover.GetProperty("event").GetProperty("sequence").GetInt64());
            await SqlAsync(db, $"UPDATE \"{db.Schema}\".customers SET name='Dora' WHERE tenant='first'", token);
            var afterTakeover = await replacementPublisher.NextAsync(token);
            Assert.Equal("RowUpdated", afterTakeover.GetProperty("event").GetProperty("kind").GetString());
            Assert.Equal("Dora", afterTakeover.GetProperty("event").GetProperty("row").GetProperty("Value").GetProperty("CustomerName").GetString());
            Assert.Equal(2, (await db.Store.ReadPublicationAsync("orders", token)).Version);
            var ddlResume = afterTakeover.GetProperty("resumeToken").GetString()!;
            var maintenanceId = Guid.NewGuid();
            using (var maintenance = await candidateHttp.PostAsync("/sample/maintenance", new StringContent(JsonSerializer.Serialize(new
                { expectedActiveVersion = 2, maintenanceId, reason = "Deploy source note column" }), Encoding.UTF8, "application/json"), token))
            { Assert.Equal(HttpStatusCode.NoContent, maintenance.StatusCode); }
            rebuildProcess.Kill(entireProcessTree: true); await rebuildProcess.WaitForExitAsync(token); rebuildProcess.Dispose(); rebuildProcess = null;
            await SqlAsync(db, $"UPDATE \"{db.Schema}\".projection_live_replay SET expires_at=clock_timestamp()-interval '1 second'; ALTER TABLE \"{db.Schema}\".orders ADD COLUMN note text NOT NULL DEFAULT 'migration'", token);
            var recoveryId = Guid.NewGuid();
            rebuildProcess = Start(db, publication, recoverySlot, rebuildPort, apiKey, 3, recoveryId);
            await ReadyAsync(candidateHttp, rebuildProcess, token);
            await using var recoverySubscriber = await ConnectAsync(candidateHttp, ddlResume, token);
            var recoveryRestart = await recoverySubscriber.NextAsync(token);
            Assert.Equal("ServerRestart", recoveryRestart.GetProperty("event").GetProperty("resetReason").GetString());
            Assert.Equal(2, recoveryRestart.GetProperty("event").GetProperty("rows")[0].GetProperty("PublishedVersion").GetInt32());
            using (var recover = await candidateHttp.PostAsync("/sample/recover", new StringContent(string.Empty), token))
            { Assert.Equal(HttpStatusCode.NoContent, recover.StatusCode); }
            var recoveredReset = await recoverySubscriber.NextAsync(token);
            Assert.Equal("SchemaChanged", recoveredReset.GetProperty("event").GetProperty("resetReason").GetString());
            Assert.Equal(3, recoveredReset.GetProperty("event").GetProperty("rows")[0].GetProperty("PublishedVersion").GetInt32());
            await SqlAsync(db, $"UPDATE \"{db.Schema}\".customers SET name='Erin' WHERE tenant='first'", token);
            var recoveredUpdate = await recoverySubscriber.NextAsync(token);
            Assert.Equal("RowUpdated", recoveredUpdate.GetProperty("event").GetProperty("kind").GetString());
            Assert.Equal("Erin", recoveredUpdate.GetProperty("event").GetProperty("row").GetProperty("Value").GetProperty("CustomerName").GetString());
            Assert.True((await db.Store.ReadRecoveryAsync("orders", recoveryId, token))!.IsComplete);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                process.Dispose();
            }
            if (rebuildProcess is not null)
            {
                if (!rebuildProcess.HasExited) { rebuildProcess.Kill(entireProcessTree: true); await rebuildProcess.WaitForExitAsync(CancellationToken.None); }
                rebuildProcess.Dispose();
            }
            await SqlAsync(db, $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name IN ('{slot}','{rebuildSlot}','{recoverySlot}')", CancellationToken.None);
            await SqlAsync(db, $"DROP PUBLICATION IF EXISTS \"{publication}\"", CancellationToken.None);
        }
    }

    private static Process Start(ProjectionDatabase db, string publication, string slot, int port, string apiKey, int version = 1, Guid? recoveryId = null)
    {
        var projectRoot = FindRoot();
        var executable = Path.Combine(projectRoot, "samples", "BlueTusk.Projections.Orders.Live", "bin", "Release", "net10.0", "BlueTusk.Projections.Orders.Live.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(executable);
        start.Environment["BLUETUSK_SAMPLE_CONNECTION_STRING"] = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        start.Environment["BLUETUSK_SAMPLE_API_KEY"] = apiKey;
        start.Environment["BLUETUSK_SAMPLE_RESUME_KEY"] = Convert.ToBase64String(new byte[32]);
        start.Environment["BLUETUSK_SAMPLE_SOURCE_SCHEMA"] = db.Schema;
        start.Environment["BLUETUSK_SAMPLE_PROJECTION_SCHEMA"] = db.Schema;
        start.Environment["BLUETUSK_SAMPLE_SLOT"] = slot;
        start.Environment["BLUETUSK_SAMPLE_PUBLICATION"] = publication;
        start.Environment["BLUETUSK_SAMPLE_VERSION"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (recoveryId is { } id)
        {
            start.Environment["BLUETUSK_SAMPLE_RECOVERY_ID"] = id.ToString("D");
            start.Environment["BLUETUSK_SAMPLE_RECOVERY_ACTIVE_VERSION"] = "2";
            start.Environment["BLUETUSK_SAMPLE_RECOVERY_REASON"] = "Deploy source note column";
        }
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:" + port;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The sample process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task ReadyAsync(HttpClient http, Process process, CancellationToken token)
    {
        while (true)
        {
            Assert.False(process.HasExited, "The deployed sample process exited before it became ready.");
            try
            {
                using var response = await http.GetAsync("/sample/status", token);
                if (response.IsSuccessStatusCode)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    if (body.RootElement.GetProperty("version").ValueKind != JsonValueKind.Null) { return; }
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(20, token);
        }
    }

    private static async Task<SseConnection> ConnectAsync(HttpClient http, string? resume, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bluetusk/live/sse")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = "orders", parameters = new { minimumAmount = 0 }, resumeToken = resume }), Encoding.UTF8, "application/json")
        };
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new SseConnection(response, new StreamReader(await response.Content.ReadAsStreamAsync(token)));
    }

    private static async Task SqlAsync(ProjectionDatabase db, string sql, CancellationToken token)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed class SseConnection(HttpResponseMessage response, StreamReader reader) : IAsyncDisposable
    {
        internal async Task<JsonElement> NextAsync(CancellationToken token)
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) { continue; }
                using var json = JsonDocument.Parse(line.AsSpan(6).ToString());
                return json.RootElement.Clone();
            }
            throw new IOException("The deployed sample closed its SSE connection.");
        }
        public ValueTask DisposeAsync() { reader.Dispose(); response.Dispose(); return ValueTask.CompletedTask; }
    }
}
