using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tests;

public sealed class PostgreSqlSchemaDeploymentCoordinatorTests
{
    [Fact]
    public async Task Transactional_sql_and_journal_are_atomic_bound_and_idempotent_after_reopen()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition("ALTER TABLE app.items ADD COLUMN label text");
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await coordinator.InitializeAsync();
        await coordinator.RegisterAsync("release_a", definition);
        await coordinator.RegisterAsync("release_a", definition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RegisterAsync("release_a",
            Definition("ALTER TABLE app.items ADD COLUMN different text")).AsTask());

        var lease = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_a", definition,
            "worker_a", TimeSpan.FromSeconds(10)));
        Assert.Null(await coordinator.AcquireAsync("release_a", definition, "worker_b", TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteTransactionalSqlStepAsync(lease,
            definition, "expand").AsTask());
        var baseline = await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline");
        Assert.True(baseline.IsNew);
        Assert.Equal(SchemaDeploymentAttemptState.Pending, baseline.State);
        Assert.False((await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline")).IsNew);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CompleteExternalStepAsync(lease,
            definition, "verify-baseline", baseline.Attempt, Digest("evidence"), definition.Plan.AfterFingerprint).AsTask());
        await coordinator.CompleteExternalStepAsync(lease, definition, "verify-baseline", baseline.Attempt,
            Digest("captured-baseline"), definition.Plan.BeforeFingerprint);
        var sql = await coordinator.ExecuteTransactionalSqlStepAsync(lease, definition, "expand");
        Assert.True(sql.IsNew); Assert.Equal(SchemaDeploymentAttemptState.Completed, sql.State);
        Assert.Equal(1, await ScalarAsync(fixture.Source,
            "SELECT count(*)::integer FROM pg_catalog.pg_attribute WHERE attrelid = 'app.items'::regclass AND attname = 'label' AND NOT attisdropped"));
        var reopened = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await reopened.InitializeAsync();
        Assert.False((await reopened.ExecuteTransactionalSqlStepAsync(lease, definition, "expand")).IsNew);
        Assert.Equal(sql.ActionFingerprint, (await reopened.ReadStepAsync("release_a", definition, "expand"))!.EvidenceSha256);
        Assert.True(await coordinator.RenewAsync(lease, definition, TimeSpan.FromSeconds(10)));
        Assert.True(await coordinator.ReleaseAsync(lease, definition));
        Assert.False(await coordinator.RenewAsync(lease, definition, TimeSpan.FromSeconds(10)));
        var successor = Assert.IsType<SchemaDeploymentLease>(await reopened.AcquireAsync("release_a", definition,
            "worker_b", TimeSpan.FromSeconds(10)));
        Assert.True(successor.FencingToken > lease.FencingToken);
    }

    [Fact]
    public async Task External_pending_attempt_requires_explicit_reconciliation_and_old_fence_cannot_finish()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition("ALTER TABLE app.items ADD COLUMN label text");
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await coordinator.InitializeAsync(); await coordinator.RegisterAsync("release_b", definition);
        var old = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_b", definition,
            "worker_a", TimeSpan.FromSeconds(1)));
        var pending = await coordinator.BeginExternalStepAsync(old, definition, "verify-baseline");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var current = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_b", definition,
            "worker_b", TimeSpan.FromSeconds(10)));
        Assert.True(current.FencingToken > old.FencingToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CompleteExternalStepAsync(old, definition,
            "verify-baseline", pending.Attempt, Digest("old"), definition.Plan.BeforeFingerprint).AsTask());
        var unknown = await coordinator.BeginExternalStepAsync(current, definition, "verify-baseline");
        Assert.False(unknown.IsNew); Assert.Equal(pending.Attempt, unknown.Attempt);
        await coordinator.ReconcileNotAppliedAsync(current, definition, "verify-baseline", unknown.Attempt,
            Digest("verified-no-effect"));
        var retry = await coordinator.BeginExternalStepAsync(current, definition, "verify-baseline");
        Assert.True(retry.IsNew); Assert.Equal(pending.Attempt + 1, retry.Attempt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CompleteExternalStepAsync(current,
            definition, "verify-baseline", pending.Attempt, Digest("stale-attempt"), definition.Plan.BeforeFingerprint).AsTask());
        await coordinator.CompleteExternalStepAsync(current, definition, "verify-baseline", retry.Attempt,
            Digest("actual-capture"), definition.Plan.BeforeFingerprint);
        var completed = await coordinator.ReadStepAsync("release_b", definition, "verify-baseline");
        Assert.Equal(SchemaDeploymentAttemptState.Completed, completed!.State);
        Assert.Equal(retry.Attempt, completed.Attempt);
    }

    [Fact]
    public async Task Prepared_sql_rejects_multiple_statements_and_rollback_keeps_step_unfinished()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition("ALTER TABLE app.items ADD COLUMN first text; ALTER TABLE app.items ADD COLUMN second text");
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await coordinator.InitializeAsync(); await coordinator.RegisterAsync("release_c", definition);
        var lease = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_c", definition,
            "worker", TimeSpan.FromSeconds(10)));
        var baseline = await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline");
        await coordinator.CompleteExternalStepAsync(lease, definition, "verify-baseline", baseline.Attempt,
            Digest("baseline"), definition.Plan.BeforeFingerprint);
        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ExecuteTransactionalSqlStepAsync(lease,
            definition, "expand").AsTask());
        Assert.Null(await coordinator.ReadStepAsync("release_c", definition, "expand"));
        Assert.Equal(0, await ScalarAsync(fixture.Source,
            "SELECT count(*)::integer FROM pg_catalog.pg_attribute WHERE attrelid = 'app.items'::regclass AND attname IN ('first', 'second') AND NOT attisdropped"));
    }

    [Fact]
    public async Task Multiple_consumer_prerequisites_require_every_completed_journal_record()
    {
        await using var fixture = await Fixture.CreateAsync();
        var consumers = new[]
        {
            new SchemaDeploymentConsumer("consumer,with,commas", [new("relation", new("app", "items"))], rebuildOnChange: false),
            new SchemaDeploymentConsumer(new string('x', 128), [new("relation", new("app", "items"))], rebuildOnChange: false),
        };
        var before = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "integer", false)])]));
        var after = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "integer", false), new("label", 2, "text", true)])]));
        var plan = SchemaDeploymentPlan.Create(before, after, consumers);
        var definition = new SchemaDeploymentDefinition(plan, plan.Steps.Select(step => new SchemaDeploymentAction(
            step.Id, step.Phase == SchemaDeploymentPhase.Expand
                ? SchemaDeploymentActionKind.TransactionalSql : SchemaDeploymentActionKind.External,
            step.Phase == SchemaDeploymentPhase.Expand ? "ALTER TABLE app.items ADD COLUMN label text" : "host:" + step.Id)));
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await coordinator.InitializeAsync(); await coordinator.RegisterAsync("release_fanin", definition);
        var lease = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_fanin", definition,
            "worker", TimeSpan.FromSeconds(10)));
        var baseline = await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline");
        await coordinator.CompleteExternalStepAsync(lease, definition, "verify-baseline", baseline.Attempt,
            Digest("baseline"), plan.BeforeFingerprint);
        var deploys = plan.Steps.Where(step => step.Phase == SchemaDeploymentPhase.DeployCompatibleConsumers).ToArray();
        Assert.Equal(2, deploys.Length);
        var first = await coordinator.BeginExternalStepAsync(lease, definition, deploys[0].Id);
        await coordinator.CompleteExternalStepAsync(lease, definition, first.StepId, first.Attempt, Digest("deployed-first"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.BeginExternalStepAsync(lease,
            definition, "fence-delivery").AsTask());
        var second = await coordinator.BeginExternalStepAsync(lease, definition, deploys[1].Id);
        await coordinator.CompleteExternalStepAsync(lease, definition, second.StepId, second.Attempt, Digest("deployed-second"));
        var fence = await coordinator.BeginExternalStepAsync(lease, definition, "fence-delivery");
        await coordinator.CompleteExternalStepAsync(lease, definition, fence.StepId, fence.Attempt, Digest("source-fenced"));
        Assert.Equal(SchemaDeploymentAttemptState.Completed,
            (await coordinator.ExecuteTransactionalSqlStepAsync(lease, definition, "expand")).State);
    }

    [Fact]
    public async Task Ddl_finishing_after_database_clock_lease_expiry_rolls_back_with_its_journal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition("ALTER TABLE app.items ADD COLUMN too_late text");
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await coordinator.InitializeAsync(); await coordinator.RegisterAsync("release_expiry", definition);
        var lease = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_expiry", definition,
            "worker_a", TimeSpan.FromSeconds(1)));
        var baseline = await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline");
        await coordinator.CompleteExternalStepAsync(lease, definition, "verify-baseline", baseline.Attempt,
            Digest("baseline"), definition.Plan.BeforeFingerprint);
        await using var blocker = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = blockerTransaction;
            command.CommandText = "SELECT count(*) FROM app.items";
            _ = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
        var running = coordinator.ExecuteTransactionalSqlStepAsync(lease, definition, "expand").AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted);
        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.Null(await coordinator.ReadStepAsync("release_expiry", definition, "expand"));
        Assert.Equal(0, await ScalarAsync(fixture.Source,
            "SELECT count(*)::integer FROM pg_catalog.pg_attribute WHERE attrelid = 'app.items'::regclass AND attname = 'too_late' AND NOT attisdropped"));
        var replacement = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync("release_expiry",
            definition, "worker_b", TimeSpan.FromSeconds(10)));
        Assert.True(replacement.FencingToken > lease.FencingToken);
    }

    [Fact]
    public async Task Reopen_rejects_unknown_format_and_changed_journal_column_shape()
    {
        await using var fixture = await Fixture.CreateAsync();
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, fixture.JournalSchema);
        await coordinator.InitializeAsync();
        await using (var command = fixture.Source.CreateCommand(
            $"UPDATE \"{fixture.JournalSchema}\".settings SET format_version = 99"))
        { _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync().AsTask());
        await using (var command = fixture.Source.CreateCommand(
            $"UPDATE \"{fixture.JournalSchema}\".settings SET format_version = 1"))
        { _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
        await using (var command = fixture.Source.CreateCommand(
            $"ALTER TABLE \"{fixture.JournalSchema}\".step_attempts ALTER COLUMN step_id TYPE varchar(120)"))
        { _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync().AsTask());
        await using (var command = fixture.Source.CreateCommand(
            $"DROP TABLE \"{fixture.JournalSchema}\".step_attempts"))
        { _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync().AsTask());
    }

    [Fact]
    public void Definition_admission_binds_every_step_and_disallows_sql_in_manual_phases()
    {
        var plan = Plan();
        Assert.Throws<ArgumentException>(() => new SchemaDeploymentDefinition(plan, []));
        Assert.Throws<ArgumentException>(() => new SchemaDeploymentDefinition(plan,
            plan.Steps.Select(step => new SchemaDeploymentAction(step.Id, SchemaDeploymentActionKind.TransactionalSql,
                "CREATE TABLE app.bad(id integer)"))));
        Assert.Throws<ArgumentException>(() => new SchemaDeploymentAction("expand", SchemaDeploymentActionKind.TransactionalSql,
            "BEGIN"));
        Assert.NotEqual(Definition("ALTER TABLE app.items ADD COLUMN label text").Fingerprint,
            Definition("ALTER TABLE app.items ADD COLUMN label varchar").Fingerprint);
    }

    [Fact]
    public void Maximum_length_and_comma_containing_consumer_names_have_unambiguous_action_bindings()
    {
        var before = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "integer", false)])]));
        var after = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "bigint", false)])]));
        var plan = SchemaDeploymentPlan.Create(before, after,
        [
            new("consumer,with,commas", [new("relation", new("app", "items"))]),
            new(new string('x', 128), [new("relation", new("app", "items"))]),
        ]);
        var definition = new SchemaDeploymentDefinition(plan,
            plan.Steps.Select(step => new SchemaDeploymentAction(step.Id, SchemaDeploymentActionKind.External,
                "external:" + step.Id)));
        Assert.Equal(plan.Steps.Count, definition.Actions.Count);
        Assert.Equal(2, plan.AffectedConsumers.Count);
        Assert.All(definition.Actions, action => Assert.InRange(Encoding.UTF8.GetByteCount(action.StepId), 1, 256));
        Assert.Equal(2, Assert.Single(plan.Steps, step => step.Phase == SchemaDeploymentPhase.FenceDelivery).DependsOn.Count);
    }

    private static SchemaDeploymentDefinition Definition(string sql)
    {
        var plan = Plan();
        return new(plan, plan.Steps.Select(step => new SchemaDeploymentAction(step.Id,
            step.Phase == SchemaDeploymentPhase.Expand ? SchemaDeploymentActionKind.TransactionalSql : SchemaDeploymentActionKind.External,
            step.Phase == SchemaDeploymentPhase.Expand ? sql : "Host attests " + step.Id)));
    }

    private static SchemaDeploymentPlan Plan()
    {
        var before = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "integer", false)])]));
        var after = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "integer", false), new("label", 2, "text", true)])]));
        return SchemaDeploymentPlan.Create(before, after, []);
    }

    private static string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<int> ScalarAsync(BlueTuskDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly BlueTuskDataSource _admin;
        private readonly string _database;
        private Fixture(BlueTuskDataSource admin, BlueTuskDataSource source, string database)
        { _admin = admin; Source = source; _database = database; JournalSchema = "schema_journal_" + Guid.NewGuid().ToString("N"); }
        internal BlueTuskDataSource Source { get; }
        internal string JournalSchema { get; }

        internal static async ValueTask<Fixture> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
                ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
            var admin = BlueTuskDataSource.Create(configured);
            var database = "schema_deployment_" + Guid.NewGuid().ToString("N");
            await using (var command = admin.CreateCommand($"CREATE DATABASE \"{database}\""))
            { _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
            var settings = new BlueTuskConnectionStringBuilder(configured) { Database = database };
            var source = BlueTuskDataSource.Create(settings.ConnectionString);
            var fixture = new Fixture(admin, source, database);
            try
            {
                await using var command = source.CreateCommand("CREATE SCHEMA app; CREATE TABLE app.items(id integer PRIMARY KEY)");
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var command = _admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await _admin.DisposeAsync();
        }
    }
}
