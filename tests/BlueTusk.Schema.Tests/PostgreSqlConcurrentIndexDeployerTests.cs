using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tests;

public sealed class PostgreSqlConcurrentIndexDeployerTests
{
    [Fact]
    public void Additive_plan_requires_review_and_rejects_changed_removed_or_invalid_targets()
    {
        var before = Catalog();
        var after = Catalog(new("items_value_idx", "CREATE INDEX items_value_idx ON app.items USING btree (value)", true));
        var indexPlan = SchemaConcurrentIndexPlan.Create(before, after);
        var build = Assert.Single(indexPlan.Builds);
        Assert.Equal("CREATE INDEX CONCURRENTLY items_value_idx ON app.items USING btree (value)", build.CreateSql);
        Assert.Equal(new SchemaRelationIdentity("app", "items"), build.Relation);
        var deployment = SchemaDeploymentPlan.Create(before, after, []);
        Assert.True(deployment.RequiresReview);
        Assert.Contains(deployment.Steps, step => step.Phase == SchemaDeploymentPhase.ApproveReview);
        Assert.Contains(deployment.Steps, step => step.Phase == SchemaDeploymentPhase.Expand);
        Assert.Throws<InvalidOperationException>(() => SchemaConcurrentIndexPlan.Create(after, before));
        Assert.Throws<InvalidOperationException>(() => SchemaConcurrentIndexPlan.Create(before,
            Catalog(new("items_value_idx", "CREATE INDEX items_value_idx ON app.items USING btree (id)", false))));
        Assert.Throws<ArgumentException>(() => SchemaConcurrentIndexPlan.Create(before,
            Catalog(new("items_value_idx", "CREATE INDEX IF NOT EXISTS items_value_idx ON app.items (value)", true))));
    }

    [Fact]
    public async Task Concurrent_build_allows_a_live_writer_and_reconciles_a_valid_index_idempotently()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO app.items(id, value) SELECT g, g % 100 FROM generate_series(1, 10000) AS g");
        var indexPlan = SchemaConcurrentIndexPlan.Create(Catalog(),
            Catalog(new("items_value_idx", "CREATE INDEX items_value_idx ON app.items USING btree (value)", true)));
        var (coordinator, definition, lease, pending) = await PrepareAsync(fixture, indexPlan, "index_build_a");
        var deployer = new PostgreSqlConcurrentIndexDeployer(fixture.Source, coordinator, maximumBuildSeconds: 30);
        await using var blocker = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var hold = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = hold;
            command.CommandText = "UPDATE app.items SET value = value + 1 WHERE id = 1";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var build = deployer.ReconcileAndApplyAsync(indexPlan, lease, definition, "expand", pending,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).AsTask();
        _ = await WaitForIndexProgressAsync(fixture.Source);
        var writer = fixture.ExecuteAsync("INSERT INTO app.items(id, value) VALUES (20001, 7)");
        try
        {
            var completed = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Same(writer, completed);
            await writer;
            Assert.False(build.IsCompleted);
        }
        finally { await hold.CommitAsync(TestContext.Current.CancellationToken); }
        var evidence = await build;
        Assert.Equal(64, evidence.Length);
        Assert.Equal(SchemaDeploymentAttemptState.Completed,
            (await coordinator.ReadStepAsync(lease.DeploymentId, definition, "expand"))!.State);
        Assert.Equal(1, await fixture.ScalarAsync("""
            SELECT count(*)::integer FROM pg_catalog.pg_index AS i
            JOIN pg_catalog.pg_class AS c ON c.oid = i.indexrelid
            WHERE c.relname = 'items_value_idx' AND i.indisvalid AND i.indisready AND i.indislive
            """));

        // A fresh deployment with the same declaration observes the already valid index; it does not issue CREATE again.
        var (againCoordinator, againDefinition, againLease, againPending) =
            await PrepareAsync(fixture, indexPlan, "index_build_b", fixture.JournalSchema);
        var again = new PostgreSqlConcurrentIndexDeployer(fixture.Source, againCoordinator, maximumBuildSeconds: 30);
        Assert.Equal(evidence, await again.ReconcileAndApplyAsync(indexPlan, againLease, againDefinition,
            "expand", againPending, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_unique_index_blocks_replay_until_explicit_cleanup_then_recovers()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO app.items(id, value) VALUES (1, 7), (2, 7)");
        var plan = SchemaConcurrentIndexPlan.Create(Catalog(),
            Catalog(new("items_value_unique", "CREATE UNIQUE INDEX items_value_unique ON app.items USING btree (value)", true)));
        var (coordinator, definition, lease, pending) = await PrepareAsync(fixture, plan, "invalid_a");
        var deployer = new PostgreSqlConcurrentIndexDeployer(fixture.Source, coordinator, maximumBuildSeconds: 30);
        await Assert.ThrowsAsync<SchemaConcurrentIndexInvalidException>(() => deployer.ReconcileAndApplyAsync(plan, lease, definition,
            "expand", pending, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(SchemaDeploymentAttemptState.Pending,
            (await coordinator.ReadStepAsync(lease.DeploymentId, definition, "expand"))!.State);
        Assert.Equal(1, await fixture.ScalarAsync("""
            SELECT count(*)::integer FROM pg_catalog.pg_index AS i
            JOIN pg_catalog.pg_class AS c ON c.oid = i.indexrelid
            WHERE c.relname = 'items_value_unique' AND NOT i.indisvalid
            """));
        await Assert.ThrowsAsync<SchemaConcurrentIndexInvalidException>(() => deployer.ReconcileAndApplyAsync(
            plan, lease, definition, "expand", pending, TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).AsTask());
        await fixture.ExecuteAsync("DROP INDEX CONCURRENTLY app.items_value_unique");
        await fixture.ExecuteAsync("DELETE FROM app.items WHERE id = 2");
        var evidence = await deployer.ReconcileAndApplyAsync(plan, lease, definition, "expand", pending,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(64, evidence.Length);
        Assert.Equal(SchemaDeploymentAttemptState.Completed,
            (await coordinator.ReadStepAsync(lease.DeploymentId, definition, "expand"))!.State);
    }

    [Fact]
    public async Task Same_name_with_an_unreviewed_definition_fails_closed_without_journal_completion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("CREATE INDEX items_value_idx ON app.items(id)");
        var plan = SchemaConcurrentIndexPlan.Create(Catalog(),
            Catalog(new("items_value_idx", "CREATE INDEX items_value_idx ON app.items USING btree (value)", true)));
        var (coordinator, definition, lease, pending) = await PrepareAsync(fixture, plan, "drift_a");
        var deployer = new PostgreSqlConcurrentIndexDeployer(fixture.Source, coordinator, maximumBuildSeconds: 30);
        await Assert.ThrowsAsync<SchemaConcurrentIndexDriftException>(() => deployer.ReconcileAndApplyAsync(
            plan, lease, definition, "expand", pending, TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(SchemaDeploymentAttemptState.Pending,
            (await coordinator.ReadStepAsync(lease.DeploymentId, definition, "expand"))!.State);
    }

    [Fact]
    public async Task Index_build_and_journal_require_the_same_data_source_instance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var plan = SchemaConcurrentIndexPlan.Create(Catalog(),
            Catalog(new("items_value_idx", "CREATE INDEX items_value_idx ON app.items USING btree (value)", true)));
        var (coordinator, _, _, _) = await PrepareAsync(fixture, plan, "source_a");
        await using var otherSource = BlueTuskDataSource.Create(
            Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")!);
        Assert.Throws<ArgumentException>(() => new PostgreSqlConcurrentIndexDeployer(otherSource, coordinator));
    }

    [Fact]
    public async Task Terminated_concurrent_build_requires_catalog_reconciliation_before_resuming_pending_attempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO app.items(id, value) SELECT g, g FROM generate_series(1, 10000) AS g");
        var plan = SchemaConcurrentIndexPlan.Create(Catalog(),
            Catalog(new("items_value_interrupted", "CREATE INDEX items_value_interrupted ON app.items USING btree (value)", true)));
        var (coordinator, definition, lease, pending) = await PrepareAsync(fixture, plan, "interrupted_a");
        var deployer = new PostgreSqlConcurrentIndexDeployer(fixture.Source, coordinator, maximumBuildSeconds: 30);
        await using var blocker = await fixture.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var hold = await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = hold;
            command.CommandText = "UPDATE app.items SET value = value + 1 WHERE id = 1";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var running = deployer.ReconcileAndApplyAsync(plan, lease, definition, "expand", pending,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).AsTask();
        var backend = await WaitForIndexProgressAsync(fixture.Source);
        await fixture.ExecuteAsync($"SELECT pg_catalog.pg_terminate_backend({backend})");
        try { await Assert.ThrowsAnyAsync<Exception>(() => running); }
        finally { await hold.CommitAsync(TestContext.Current.CancellationToken); }
        Assert.Equal(SchemaDeploymentAttemptState.Pending,
            (await coordinator.ReadStepAsync(lease.DeploymentId, definition, "expand"))!.State);
        var invalid = await fixture.ScalarAsync("""
            SELECT count(*)::integer FROM pg_catalog.pg_index AS i
            JOIN pg_catalog.pg_class AS c ON c.oid = i.indexrelid
            WHERE c.relname = 'items_value_interrupted' AND NOT i.indisvalid
            """);
        if (invalid == 1)
        {
            await Assert.ThrowsAsync<SchemaConcurrentIndexInvalidException>(() => deployer.ReconcileAndApplyAsync(
                plan, lease, definition, "expand", pending, TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).AsTask());
            await fixture.ExecuteAsync("DROP INDEX CONCURRENTLY app.items_value_interrupted");
        }
        else
        {
            Assert.Equal(0, await fixture.ScalarAsync("""
                SELECT count(*)::integer FROM pg_catalog.pg_class AS c
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.nspname = 'app' AND c.relname = 'items_value_interrupted'
                """));
        }
        Assert.Equal(64, (await deployer.ReconcileAndApplyAsync(plan, lease, definition, "expand", pending,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Length);
        Assert.Equal(SchemaDeploymentAttemptState.Completed,
            (await coordinator.ReadStepAsync(lease.DeploymentId, definition, "expand"))!.State);
    }

    private static SchemaCatalogSnapshot Catalog(SchemaIndex? index = null)
    {
        var relation = new SchemaRelation(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "integer", false), new("value", 2, "integer", true)],
            indexes: index is null ? [] : [index]);
        return new(new([relation]));
    }

    private static async Task<(PostgreSqlSchemaDeploymentCoordinator Coordinator, SchemaDeploymentDefinition Definition,
        SchemaDeploymentLease Lease, SchemaDeploymentStepAttempt Pending)> PrepareAsync(Fixture fixture,
        SchemaConcurrentIndexPlan indexPlan, string deploymentId, string? journalSchema = null)
    {
        var before = Catalog();
        var targetIndex = indexPlan.Builds.Single();
        var after = Catalog(new(targetIndex.IndexName, targetIndex.ExpectedDefinition, true));
        var plan = SchemaDeploymentPlan.Create(before, after, []);
        Assert.Equal(indexPlan.BeforeFingerprint, plan.BeforeFingerprint);
        Assert.Equal(indexPlan.AfterFingerprint, plan.AfterFingerprint);
        var definition = new SchemaDeploymentDefinition(plan, plan.Steps.Select(step => new SchemaDeploymentAction(
            step.Id, SchemaDeploymentActionKind.External,
            step.Phase == SchemaDeploymentPhase.Expand ? indexPlan.ActionContent : "host:" + step.Id)));
        var coordinator = new PostgreSqlSchemaDeploymentCoordinator(fixture.Source, journalSchema ?? fixture.JournalSchema);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);
        await coordinator.RegisterAsync(deploymentId, definition, TestContext.Current.CancellationToken);
        var lease = Assert.IsType<SchemaDeploymentLease>(await coordinator.AcquireAsync(deploymentId, definition,
            "index_worker", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var baseline = await coordinator.BeginExternalStepAsync(lease, definition, "verify-baseline",
            TestContext.Current.CancellationToken);
        await coordinator.CompleteExternalStepAsync(lease, definition, "verify-baseline", baseline.Attempt,
            Digest("baseline"), plan.BeforeFingerprint, TestContext.Current.CancellationToken);
        var review = await coordinator.BeginExternalStepAsync(lease, definition, "approve-review",
            TestContext.Current.CancellationToken);
        await coordinator.CompleteExternalStepAsync(lease, definition, "approve-review", review.Attempt,
            Digest("reviewed-index-sql"), plan.Fingerprint, TestContext.Current.CancellationToken);
        var pending = await coordinator.BeginExternalStepAsync(lease, definition, "expand",
            TestContext.Current.CancellationToken);
        return (coordinator, definition, lease, pending);
    }

    private static async Task<int> WaitForIndexProgressAsync(BlueTuskDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = source.CreateCommand("""
                SELECT pid::integer FROM pg_catalog.pg_stat_progress_create_index
                WHERE relid = 'app.items'::regclass AND command = 'CREATE INDEX CONCURRENTLY' LIMIT 1
                """);
            if (await command.ExecuteScalarAsync(deadline.Token) is int pid) { return pid; }
            await Task.Delay(50, deadline.Token);
        }
    }

    private static string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly BlueTuskDataSource _admin;
        private readonly string _database;
        private Fixture(BlueTuskDataSource admin, BlueTuskDataSource source, string database)
        { _admin = admin; Source = source; _database = database; JournalSchema = "schema_indexes_" + Guid.NewGuid().ToString("N"); }
        internal BlueTuskDataSource Source { get; }
        internal string JournalSchema { get; }

        internal static async ValueTask<Fixture> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
                ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
            var admin = BlueTuskDataSource.Create(configured);
            var database = "schema_indexes_" + Guid.NewGuid().ToString("N");
            await using (var command = admin.CreateCommand($"CREATE DATABASE \"{database}\""))
            { _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
            var settings = new BlueTuskConnectionStringBuilder(configured) { Database = database };
            var source = BlueTuskDataSource.Create(settings.ConnectionString);
            var fixture = new Fixture(admin, source, database);
            try
            {
                await fixture.ExecuteAsync("CREATE SCHEMA app; CREATE TABLE app.items(id integer PRIMARY KEY, value integer)");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var command = Source.CreateCommand(sql);
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        internal async Task<int> ScalarAsync(string sql)
        {
            await using var command = Source.CreateCommand(sql);
            return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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
