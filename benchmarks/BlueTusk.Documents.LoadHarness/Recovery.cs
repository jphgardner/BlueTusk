using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

internal static partial class Program
{
    private static async Task<RecoveryReport> RunRecoveryAsync(string raw, BlueTuskDataSource observer, CancellationToken token)
    {
        var schema = "docs_crash_" + Guid.NewGuid().ToString("N"); var childApp = Application + "Child";
        const long barrier = 180942166011;
        await using var source = CreateSource(raw, Application + "Recovery", 4);
        await using var store = new DocumentStore(source, new() { Schema = schema, CommandsPerBatch = 1 });
        await using var blocker = await observer.OpenConnectionAsync(token);
        Process? child = null;
        try
        {
            await store.InitializeAsync(token);
            await using (var setup = new BlueTuskCommand($"""
                CREATE FUNCTION "{schema}".block_second_insert() RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN IF NEW.id='blocked-z' THEN PERFORM pg_advisory_xact_lock({barrier}); END IF; RETURN NEW; END;$function$;
                CREATE TRIGGER crash_barrier BEFORE INSERT ON "{schema}".documents FOR EACH ROW EXECUTE FUNCTION "{schema}".block_second_insert();
                """, blocker)) { _ = await setup.ExecuteNonQueryAsync(token); }
            await using (var acquire = new BlueTuskCommand($"SELECT pg_advisory_lock({barrier})", blocker)) { _ = await acquire.ExecuteNonQueryAsync(token); }
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--crash-child"); start.ArgumentList.Add(schema);
            start.Environment["BLUETUSK_DOCUMENTS_LOAD_CONNECTION_STRING"] = raw;
            child = Process.Start(start) ?? throw new InvalidOperationException("Owned recovery child failed to start.");
            var errors = child.StandardError.ReadToEndAsync(token); const int batches = 16;
            for (var batch = 0; batch < batches; batch++)
            {
                var line = await child.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
                Check(line == "ACK " + batch.ToString(CultureInfo.InvariantCulture), "parent observed only completed successful saves");
            }
            Check(await child.StandardOutput.ReadLineAsync(token) == "BLOCK_START", "child starts blocked transaction after acknowledgements");
            var blocked = false;
            for (var attempt = 0; attempt < 200 && !blocked; attempt++)
            {
                await using var probe = observer.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name=@application AND wait_event_type='Lock' AND lower(wait_event)='advisory'");
                probe.Parameters.Add(new BlueTuskParameter<string>(childApp) { ParameterName = "application" });
                blocked = (long)(await probe.ExecuteScalarAsync(token))! == 1;
                if (!blocked) { await Task.Delay(25, token); }
            }
            Check(blocked, "actual database barrier reached on second insert");
            var killed = Stopwatch.GetTimestamp(); child.Kill(entireProcessTree: true); await child.WaitForExitAsync(token); _ = await errors;
            // Release only the parent-owned barrier. The killed client cannot send COMMIT; PostgreSQL observes EOF and rolls back.
            await using (var release = new BlueTuskCommand($"SELECT pg_advisory_unlock({barrier})", blocker)) { Check(await release.ExecuteScalarAsync(token) is true, "owned barrier released"); }
            for (var attempt = 0; attempt < 200; attempt++)
            {
                await using var idle = observer.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name=@application");
                idle.Parameters.Add(new BlueTuskParameter<string>(childApp) { ParameterName = "application" });
                if ((long)(await idle.ExecuteScalarAsync(token))! == 0) { break; }
                Check(attempt < 199, "killed child backend drained"); await Task.Delay(25, token);
            }
            await using var reopened = new DocumentStore(source, store.Options);
            for (var batch = 0; batch < batches; batch++)
                for (var row = 0; row < 2; row++)
                {
                    var value = await reopened.LoadAsync(Tenant(batch % 4), Collection, $"ack-{batch:D2}-{row}", token);
                    Check(value?.Value.Count == batch && value.Value.TenantMarker == Tenant(batch % 4) && value.Value.Payload == Payload(64 * 1024), "all previously acknowledged payloads survived reopen");
                    Check(await reopened.LoadAsync(Tenant((batch + 1) % 4), Collection, $"ack-{batch:D2}-{row}", token) is null, "no recovery cross-tenant visibility");
                }
            Check(await reopened.LoadAsync(Tenant(0), Collection, "blocked-a", token) is null && await reopened.LoadAsync(Tenant(0), Collection, "blocked-z", token) is null,
                "whole unacknowledged multi-document transaction rolled back");
            var current = (await reopened.LoadAsync(Tenant(0), Collection, "ack-00-0", token))!;
            using (var deletion = reopened.OpenSession(Tenant(0))) { deletion.Delete(Collection, current.Id, current.Revision); _ = await deletion.SaveChangesAsync(token); }
            using (var insertion = reopened.OpenSession(Tenant(0))) { insertion.Insert(Collection, current.Id, current.Value); _ = await insertion.SaveChangesAsync(token); }
            var staleRejected = false;
            using (var stale = reopened.OpenSession(Tenant(0)))
            {
                stale.Replace(Collection, current.Id, current.Value with { Count = 999 }, current.Revision);
                try { _ = await stale.SaveChangesAsync(token); } catch (DocumentConcurrencyException) { staleRejected = true; }
            }
            Check(staleRejected, "pre-crash revision cannot mutate reinserted identity");
            return new(batches, batches * 2, 4, blocked, true, true, true, true, staleRejected, Stopwatch.GetElapsedTime(killed).TotalMilliseconds);
        }
        finally
        {
            if (child is not null) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); } child.Dispose(); }
            await using (var release = new BlueTuskCommand("SELECT pg_advisory_unlock_all()", blocker) { CommandTimeout = 10 }) { _ = await release.ExecuteNonQueryAsync(CancellationToken.None); }
            await DropSchemaAsync(observer, schema);
        }
    }
    private static async Task CrashChildAsync(string schema)
    {
        Check(schema.Length == "docs_crash_".Length + 32 && schema.StartsWith("docs_crash_", StringComparison.Ordinal)
            && schema["docs_crash_".Length..].All(Uri.IsHexDigit), "parent-generated recovery schema");
        var raw = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Owned child database is missing.");
        await using var source = CreateSource(raw, Application + "Child", 1);
        await using var store = new DocumentStore(source, new() { Schema = schema, CommandsPerBatch = 1 }); var payload = Payload(64 * 1024);
        for (var batch = 0; batch < 16; batch++)
        {
            using var session = store.OpenSession(Tenant(batch % 4));
            for (var row = 0; row < 2; row++) { session.Insert(Collection, $"ack-{batch:D2}-{row}", new(Tenant(batch % 4), payload, batch)); }
            Check((await session.SaveChangesAsync()).Count == 2, "child acknowledged complete save");
            Console.WriteLine("ACK " + batch.ToString(CultureInfo.InvariantCulture)); await Console.Out.FlushAsync();
        }
        using var blocked = store.OpenSession(Tenant(0)); blocked.Insert(Collection, "blocked-a", new(Tenant(0), payload, 0)); blocked.Insert(Collection, "blocked-z", new(Tenant(0), payload, 0));
        Console.WriteLine("BLOCK_START"); await Console.Out.FlushAsync(); _ = await blocked.SaveChangesAsync();
        throw new InvalidOperationException("Child unexpectedly completed the parent-blocked save.");
    }
}
