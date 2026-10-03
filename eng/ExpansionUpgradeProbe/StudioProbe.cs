using System.Globalization;
using BlueTusk.Studio;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Studio audit retention rehearsal. The baseline records audits in durable version 3, seals and
/// confirms one archive horizon without pruning, and seals a second horizon it never confirms. The
/// candidate must keep version 3, read every audit, treat an identical retry as a no-op, reject a new
/// row under the sealed horizon, prune exactly the confirmed rows, confirm the pending horizon and a
/// legacy archive, and reject a newer version. The baseline then prunes only through the candidate's
/// confirmation and keeps its legacy confirmation.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Studio";
    private static readonly string QueryFingerprint = new('a', 64);

    public static async Task RunAsync(ProbeContext context)
    {
        var schema = context.SchemaBase + "_studio";
        var sink = new PostgreSqlStudioAuditSink(context.DataSource, schema);
        await context.FingerprintAsync("before", schema);
        await sink.InitializeAsync();
        await context.FingerprintAsync("after", schema);
        context.Observe("AuditVersion", await context.CountAsync($"SELECT version FROM \"{schema}\".studio_audit_version"));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (context.IsSeed)
        {
            var ids = new[] { now.AddMinutes(-4), now.AddSeconds(-210), now.AddMinutes(-2), now.AddMinutes(-1) }
                .Select(Guid.CreateVersion7).ToArray();
            foreach (var id in ids)
            {
                await sink.RecordAsync(Record(id, "attempt"));
            }

            context.Observe("RecordedAudits", await CountAuditsAsync(context, schema));
            var confirmed = now.AddMinutes(-3);
            await sink.SealRetentionHorizonAsync(confirmed);
            await sink.ConfirmArchivedHorizonAsync(confirmed, "archive-1");
            var sealedOnly = now.AddSeconds(-90);
            await sink.SealRetentionHorizonAsync(sealedOnly);
            context.Observe("ArchivedUnprunedRows", await context.CountAsync(
                $"SELECT count(*) FROM \"{schema}\".studio_audit a, \"{schema}\".studio_audit_version v WHERE a.operation_time_ms <= v.archived_through_ms"));
            context.Observe("Archives", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".studio_audit_archives"));
            context.WriteState(new()
            {
                ["ids"] = string.Join(',', ids.Select(id => id.ToString("N"))),
                ["sealed"] = sealedOnly.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            });
            return;
        }

        var handoff = context.ReadState();
        var seeded = handoff["ids"].Split(',').Select(id => Guid.ParseExact(id, "N")).ToArray();
        if (context.IsUpgrade)
        {
            context.Observe("AuditsRead", await RequireAuditsAsync(context, schema, seeded));
            await sink.RecordAsync(Record(seeded[3], "attempt"));
            Require(await CountAuditsAsync(context, schema) == seeded.Length, "An identical audit retry was stored twice.");
            context.Observe("IdempotentRetries", 1);
            await RequireRejectedAsync<StudioAuditHorizonException>(async () => await sink.RecordAsync(Record(seeded[0], "completed")),
                "The candidate appended an audit under the baseline's sealed horizon.");
            context.Observe("SealedRetryRejected", 1);
            context.Observe("PrunedRows", await PruneAsync(sink));
            var sealedOnly = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(handoff["sealed"], CultureInfo.InvariantCulture));
            await sink.ConfirmArchivedHorizonAsync(sealedOnly, "archive-2");
            await sink.ConfirmLegacyArchiveAsync("legacy-1");
            context.Observe("LegacyArchiveConfirmed", await context.CountAsync(
                $"SELECT count(*) FROM \"{schema}\".studio_audit_version WHERE legacy_archived"));
            context.Observe("Archives", await context.CountAsync($"SELECT count(*) FROM \"{schema}\".studio_audit_archives"));
            var added = Guid.CreateVersion7(now);
            await sink.RecordAsync(Record(added, "attempt"));
            context.Observe("RecordedAudits", 1);
            await RequireNewerFormatRejectedAsync(context);
            handoff["ids"] = string.Join(',', seeded.Skip(2).Append(added).Select(id => id.ToString("N")));
            context.WriteState(handoff);
            return;
        }

        context.Observe("AuditsRead", await RequireAuditsAsync(context, schema, seeded));
        context.Observe("PrunedRows", await PruneAsync(sink));
        context.Observe("LegacyArchivedPreserved", await context.CountAsync(
            $"SELECT count(*) FROM \"{schema}\".studio_audit_version WHERE legacy_archived"));
        await sink.RecordAsync(Record(Guid.CreateVersion7(now), "attempt"));
        context.Observe("RecordedAudits", 1);
    }

    private static StudioAuditRecord Record(Guid id, string outcome) =>
        new(id, "upgrade-actor", QueryFingerprint, outcome, 0) { ScopeId = "upgrade-scope" };

    private static Task<long> CountAuditsAsync(ProbeContext context, string schema) =>
        context.CountAsync($"SELECT count(*) FROM \"{schema}\".studio_audit");

    private static async Task<int> RequireAuditsAsync(ProbeContext context, string schema, Guid[] expected)
    {
        var present = await context.ScalarAsync(
            $"SELECT string_agg(replace(operation_id::text, '-', ''), ',' ORDER BY operation_id) FROM \"{schema}\".studio_audit") as string ?? "";
        Require(present == string.Join(',', expected.Select(id => id.ToString("N")).Order(StringComparer.Ordinal)),
            "Audit rows were lost, duplicated or changed across the binary boundary.");
        return expected.Length;
    }

    private static async Task<int> PruneAsync(PostgreSqlStudioAuditSink sink)
    {
        var pruned = 0;
        int batch;
        while ((batch = await sink.PruneArchivedAsync(1000)) > 0)
        {
            pruned += batch;
        }

        return pruned;
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // studio/README.md: unknown durable versions reject initialization and append.
        var future = context.SchemaBase + "_future";
        var sink = new PostgreSqlStudioAuditSink(context.DataSource, future);
        await sink.InitializeAsync();
        await context.ExecuteAsync($"UPDATE \"{future}\".studio_audit_version SET version = version + 1");
        await RequireRejectedAsync<InvalidOperationException>(async () => await sink.InitializeAsync(),
            "The candidate initialized a Studio audit version newer than it supports.");
        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }
}
