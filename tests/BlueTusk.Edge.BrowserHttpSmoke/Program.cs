using System.Diagnostics;
using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Edge;
using BlueTusk.Edge.Server;
using BlueTusk.Edge.SmokeHosting;

var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Configure a disposable PostgreSQL database for browser HTTP verification.");
var schema = "edge_browser_" + Guid.NewGuid().ToString("N");
await using var source = BlueTuskDataSource.Create(connection);
await using var store = new PostgreSqlEdgeServerStore(source, new EdgeServerOptions { Schema = schema });
var browserProfile = Directory.CreateTempSubdirectory("bluetusk-edge-http-browser-").FullName;
try
{
    await store.InitializeAsync(); await store.ActivateScopeAsync(new EdgeScope("tenant", "orders", 1));
    await using (var setup = source.CreateCommand($"CREATE TABLE \"{schema}\".business_effects(id text PRIMARY KEY,counter integer NOT NULL)"))
    { _ = await setup.ExecuteNonQueryAsync(); }
    async ValueTask WriteBusinessAsync(DbConnection connection, DbTransaction transaction, EdgeMutation mutation, EdgeRecord record, CancellationToken cancellationToken)
    {
        if (record.Id != mutation.DocumentId) { throw new InvalidOperationException("Authoritative business record identity differs."); }
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"INSERT INTO \"{schema}\".business_effects VALUES(@id,1) ON CONFLICT(id) DO UPDATE SET counter=business_effects.counter+1";
        var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = mutation.DocumentId; command.Parameters.Add(id);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var host = await EdgeSmokeHost.StartAsync(store, browserCors: true, writeBusiness: WriteBusinessAsync);
    var workspace = Path.GetFullPath(args.Length == 0 ? Environment.CurrentDirectory : args[0]);
    var script = Path.Combine(workspace, "clients", "edge", "test", "http.browser.smoke.mjs");
    if (!File.Exists(script)) { throw new InvalidOperationException("Pass the BlueTusk workspace directory containing the browser test script."); }
    var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace };
    start.ArgumentList.Add(script); start.Environment["BLUETUSK_EDGE_HTTP_ENDPOINT"] = host.Endpoint.ToString();
    start.Environment["BLUETUSK_EDGE_HTTP_BROWSER_PROFILE"] = browserProfile;
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned browser verification process.");
    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
    Console.Write(await output); Console.Error.Write(await error);
    if (child.ExitCode != 0) { throw new InvalidOperationException("Actual browser HTTP verification failed."); }
    var changes = await store.ReadChangesAsync(new EdgeScope("tenant", "orders", 1), 0);
    if (changes?.ToPosition != 4 || !(await store.GetAsync(new EdgeScope("tenant", "orders", 1), "1"))!.Deleted)
    { throw new InvalidOperationException("Browser commits, replay deduplication or final tombstone differs on the server."); }
    await using var effects = source.CreateCommand($"SELECT counter FROM \"{schema}\".business_effects");
    if (await effects.ExecuteScalarAsync() is not int businessEffects || businessEffects != 4)
    { throw new InvalidOperationException("Browser lost response/restart/conflict repeated external application-table effects."); }
}
finally
{
    try { await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); _ = await cleanup.ExecuteNonQueryAsync(); }
    finally { DeleteOwnedProfile(browserProfile); }
}

static void DeleteOwnedProfile(string profile)
{
    var resolved = Path.GetFullPath(profile);
    var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("bluetusk-edge-http-browser-", StringComparison.Ordinal))
    { throw new InvalidOperationException("Refusing browser profile cleanup outside owned temporary directory."); }
    if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
}
