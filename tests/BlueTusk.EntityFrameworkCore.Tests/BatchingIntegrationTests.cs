using System.Data.Common;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class BatchingIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_interceptors_retain_the_legacy_rows_affected_result_shape(bool synchronous)
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer, extra: observer);
        context.Plain.AddRange(Enumerable.Range(1, 50).Select(i => new PlainRow { Id = i, Name = $"row-{i}" }));
        Assert.Equal(50, await SaveAsync(context, synchronous));
        foreach (var row in context.Plain.Local) { row.Name += "-updated"; }
        Assert.Equal(50, await SaveAsync(context, synchronous));
        Assert.NotEmpty(observer.Texts);
        Assert.All(observer.Texts, sql => Assert.Contains("RETURNING 1", sql, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_concurrency_interceptor_policy_is_still_observed(bool synchronous)
    {
        await using var connection = await OpenAsync();
        var policy = new SuppressConcurrency();
        await using var context = CreateContext(connection, new BatchObserver(), extra: policy);
        var row = new PlainRow { Id = 1, Name = "original" };
        context.Plain.Add(row);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM bt_batch_plain WHERE \"Id\" = 1");
        row.Name = "missing row";
        // The caller explicitly suppresses this conflict; the provider must
        // still invoke the ordinary EF logger/interceptor policy exactly once.
        Assert.Equal(1, await SaveAsync(context, synchronous));
        Assert.Equal(1, policy.Calls);
        Assert.Equal(0, await context.Plain.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writes_without_generated_values_use_command_counts_not_returning_rows(bool synchronous)
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer);
        var rows = Enumerable.Range(1, 100).Select(i => new PlainRow { Id = i, Name = $"row-{i}" }).ToArray();
        context.Plain.AddRange(rows);
        Assert.Equal(100, await SaveAsync(context, synchronous));
        foreach (var row in rows) { row.Name += "-updated"; }
        Assert.Equal(100, await SaveAsync(context, synchronous));
        context.Plain.RemoveRange(rows);
        Assert.Equal(100, await SaveAsync(context, synchronous));
        Assert.Equal(0, await context.Plain.CountAsync());
        Assert.All(observer.Texts, sql => Assert.DoesNotContain("RETURNING", sql, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stale_updates_and_deletes_use_the_current_statement_count(bool synchronous, bool delete)
    {
        await using var connection = await OpenAsync();
        await using var context = CreateContext(connection, new BatchObserver());
        var rows = Enumerable.Range(1, 100).Select(i => new PlainRow { Id = i, Name = $"row-{i}" }).ToArray();
        context.Plain.AddRange(rows);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM bt_batch_plain WHERE \"Id\" = 46");
        await using var transaction = await context.Database.BeginTransactionAsync();
        if (delete) { context.Plain.RemoveRange(rows); }
        else { foreach (var row in rows) { row.Name = "must roll back"; } }
        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => SaveAsync(context, synchronous));
        Assert.Same(rows[45], Assert.Single(failure.Entries).Entity);
        context.ChangeTracker.Clear();
        Assert.Equal(99, await context.Plain.CountAsync());
        Assert.Equal(0, await context.Plain.CountAsync(row => row.Name == "must roll back"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trigger_suppressed_inserts_are_not_reported_as_successful_writes(bool synchronous)
    {
        await using var connection = await OpenAsync();
        await using var context = CreateContext(connection, new BatchObserver());
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION pg_temp.bt_skip_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Name" = 'skip' THEN RETURN NULL; END IF; RETURN NEW; END $$;
            CREATE TRIGGER bt_skip BEFORE INSERT ON bt_batch_plain FOR EACH ROW EXECUTE FUNCTION pg_temp.bt_skip_insert();
            """);
        var rows = Enumerable.Range(1, 100).Select(i => new PlainRow { Id = i, Name = i == 46 ? "skip" : $"row-{i}" }).ToArray();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Plain.AddRange(rows);
        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => SaveAsync(context, synchronous));
        Assert.Same(rows[45], Assert.Single(failure.Entries).Entity);
        Assert.Equal(0, await context.Plain.CountAsync());
        context.ChangeTracker.Clear();
        context.Plain.Add(new PlainRow { Id = 101, Name = "recovered" });
        Assert.Equal(1, await SaveAsync(context, synchronous));
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Bounded_batches_preserve_generated_values_and_sync_async_crud(bool synchronous, bool singleCommand)
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer, singleCommand ? 1 : null);
        var rows = Enumerable.Range(1, 100).Select(i => new GeneratedRow { Name = $"row-{i}", Version = 1 }).ToArray();
        context.Generated.AddRange(rows);
        Assert.Equal(100, await SaveAsync(context, synchronous));
        Assert.Equal(singleCommand ? 100 : 3, observer.Sizes.Count);
        Assert.All(observer.Sizes, size => Assert.InRange(size, 1, singleCommand ? 1 : 42));
        Assert.Equal(100, rows.Select(row => row.Id).Distinct().Count());
        Assert.All(rows, row => { Assert.True(row.Id > 0); Assert.Equal(row.Name.Length, row.NameLength); });

        observer.Sizes.Clear();
        foreach (var row in rows) { row.Name += "-updated"; }
        Assert.Equal(100, await SaveAsync(context, synchronous));
        Assert.Equal(singleCommand ? 100 : 3, observer.Sizes.Count);
        Assert.All(rows, row => Assert.Equal(row.Name.Length, row.NameLength));
        context.ChangeTracker.Clear();
        Assert.Equal(100, await context.Generated.CountAsync(row => row.Name.EndsWith("-updated")));
        var stored = await context.Generated.ToListAsync();
        context.Generated.RemoveRange(stored);
        Assert.Equal(100, await SaveAsync(context, synchronous));
        Assert.Equal(0, await context.Generated.CountAsync());
    }

    [Fact]
    public async Task Mixed_client_and_server_generated_keys_keep_result_sets_aligned()
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer, 100);
        var generated = Enumerable.Range(1, 12).Select(i => new GeneratedRow { Name = $"generated-{i}", Version = 1 }).ToArray();
        context.Generated.AddRange(generated);
        context.Plain.AddRange(Enumerable.Range(1, 12).Select(i => new PlainRow { Id = i, Name = $"plain-{i}" }));
        Assert.Equal(24, await context.SaveChangesAsync());
        Assert.Single(observer.Sizes);
        Assert.Equal(24, observer.Sizes[0]);
        Assert.All(generated, row => { Assert.True(row.Id > 0); Assert.Equal(row.Name.Length, row.NameLength); });
        context.ChangeTracker.Clear();
        Assert.Equal(12, await context.Plain.CountAsync());
        Assert.Equal(12, await context.Generated.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Middle_batch_concurrency_failure_rolls_back_to_the_callers_savepoint(bool synchronous)
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer);
        context.Generated.AddRange(Enumerable.Range(1, 10).Select(i => new GeneratedRow { Name = $"original-{i}", Version = 1 }));
        await context.SaveChangesAsync();
        var rows = context.Generated.Local.OrderBy(row => row.Id).ToArray();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("INSERT INTO bt_batch_plain VALUES (1000, 'keep me')");
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE bt_batch_generated SET \"Version\" = 2 WHERE \"Id\" = {rows[4].Id}");
        foreach (var row in rows) { row.Name = "must roll back"; }
        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => SaveAsync(context, synchronous));
        Assert.Same(rows[4], Assert.Single(failure.Entries).Entity);
        Assert.Equal(0, await context.Generated.AsNoTracking().CountAsync(row => row.Name == "must roll back"));
        Assert.Equal(1, await context.Plain.CountAsync(row => row.Id == 1000));
        context.ChangeTracker.Clear();
        context.Plain.Add(new PlainRow { Id = 1001, Name = "transaction still usable" });
        Assert.Equal(1, await context.SaveChangesAsync());
        await transaction.CommitAsync();
        Assert.Equal(2, await context.Plain.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Server_failure_in_a_later_batch_does_not_leave_partial_writes(bool synchronous, bool explicitTransaction)
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer);
        await using var transaction = explicitTransaction ? await context.Database.BeginTransactionAsync() : null;
        context.Plain.AddRange(Enumerable.Range(1, 100)
            .Select(i => new PlainRow { Id = i, Name = i == 75 ? "invalid" : $"row-{i}" }));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(context, synchronous));
        Assert.Equal("23514", Assert.IsType<BlueTuskException>(failure.InnerException).SqlState);
        Assert.Equal(0, await context.Plain.AsNoTracking().CountAsync());
        context.ChangeTracker.Clear();
        context.Plain.Add(new PlainRow { Id = 101, Name = "recovered" });
        Assert.Equal(1, await context.SaveChangesAsync());
        if (transaction is not null) { await transaction.CommitAsync(); }
    }

    private static Task<int> SaveAsync(BatchContext context, bool synchronous)
        => synchronous ? Task.FromResult(context.SaveChanges()) : context.SaveChangesAsync();

    [Fact]
    public async Task Large_configured_batches_still_respect_the_aggregate_sql_bound()
    {
        await using var connection = await OpenAsync();
        var observer = new BatchObserver();
        await using var context = CreateContext(connection, observer, 1000);
        context.Plain.AddRange(Enumerable.Range(1, 1000).Select(i => new PlainRow { Id = i, Name = $"row-{i}" }));
        Assert.Equal(1000, await context.SaveChangesAsync());
        Assert.True(observer.Sizes.Count > 1);
        Assert.All(observer.TextLengths, length => Assert.InRange(length, 1, 64 * 1024));
        Assert.Equal(1000, await context.Plain.CountAsync());
    }

    [Fact]
    public async Task Synchronous_implicit_transaction_failure_rolls_back_before_discarding_the_session()
    {
        await using var connection = await OpenAsync();
        var schema = $"bt_batch_rollback_{Guid.NewGuid():N}";
        await using (var setup = new BlueTuskCommand($"""
                         CREATE SCHEMA "{schema}";
                         CREATE TABLE "{schema}".bt_batch_plain ("Id" int PRIMARY KEY,
                             "Name" text NOT NULL CHECK ("Name" <> 'invalid'));
                         DROP TABLE pg_temp.bt_batch_plain;
                         SET search_path TO "{schema}";
                         """, connection))
        {
            await setup.ExecuteNonQueryAsync();
        }
        try
        {
            await using var context = CreateContext(connection, new BatchObserver());
            context.Plain.AddRange(Enumerable.Range(1, 100)
                .Select(i => new PlainRow { Id = i, Name = i == 75 ? "invalid" : $"row-{i}" }));
            var failure = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
            Assert.Equal("23514", Assert.IsType<BlueTuskException>(failure.InnerException).SqlState);
            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
            await connection.OpenAsync();
            await using var count = new BlueTuskCommand($"SELECT count(*)::int8 FROM \"{schema}\".bt_batch_plain", connection);
            Assert.Equal(0, await count.ExecuteScalarAsync<long>());
        }
        finally
        {
            if (connection.State != System.Data.ConnectionState.Open) { await connection.OpenAsync(); }
            await using var cleanup = new BlueTuskCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static BatchContext CreateContext(BlueTuskConnection connection, BatchObserver observer, int? maxBatchSize = null,
        IInterceptor? extra = null)
    {
        var builder = new DbContextOptionsBuilder<BatchContext>().UseBlueTusk(connection.ConnectionString, configure =>
        {
            if (maxBatchSize.HasValue) { configure.MaxBatchSize(maxBatchSize.Value); }
        }).LogTo((eventId, _) => eventId == RelationalEventId.CommandExecuting, eventData =>
        {
            if (eventData is CommandEventData command) { observer.Observe(command.Command, command); }
        });
        if (extra is not null) { builder.AddInterceptors(extra); }
        var context = new BatchContext(builder.Options);
        context.Database.SetDbConnection(connection);
        return context;
    }

    private static async Task<BlueTuskConnection> OpenAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }
        var connection = new BlueTuskConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            await using var create = new BlueTuskCommand("""
                CREATE TEMP TABLE bt_batch_plain ("Id" int PRIMARY KEY, "Name" text NOT NULL CHECK ("Name" <> 'invalid'));
                CREATE TEMP TABLE bt_batch_generated ("Id" int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    "Name" text NOT NULL, "Version" int NOT NULL,
                    "NameLength" int GENERATED ALWAYS AS (length("Name")) STORED);
                SET statement_timeout = '10s';
                """, connection);
            await create.ExecuteNonQueryAsync();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class BatchObserver : DbCommandInterceptor
    {
        public List<int> Sizes { get; } = [];
        public List<int> TextLengths { get; } = [];
        public List<string> Texts { get; } = [];
        internal void Observe(DbCommand command, CommandEventData eventData)
        {
            if (eventData.CommandSource == CommandSource.SaveChanges)
            {
                Sizes.Add(command.CommandText.Count(c => c == ';'));
                TextLengths.Add(command.CommandText.Length);
                Texts.Add(command.CommandText);
            }
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        { Observe(command, eventData); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Observe(command, eventData);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SuppressConcurrency : SaveChangesInterceptor
    {
        public int Calls { get; private set; }
        public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData,
            InterceptionResult result)
        {
            Calls++;
            return InterceptionResult.Suppress();
        }
        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(InterceptionResult.Suppress());
        }
    }

    private sealed class BatchContext(DbContextOptions<BatchContext> options) : DbContext(options)
    {
        public DbSet<GeneratedRow> Generated => Set<GeneratedRow>();
        public DbSet<PlainRow> Plain => Set<PlainRow>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GeneratedRow>(entity =>
            {
                entity.ToTable("bt_batch_generated");
                entity.Property(row => row.NameLength).HasComputedColumnSql("length(\"Name\")", stored: true);
                entity.Property(row => row.Version).IsConcurrencyToken();
            });
            modelBuilder.Entity<PlainRow>(entity =>
            {
                entity.ToTable("bt_batch_plain");
                entity.Property(row => row.Id).ValueGeneratedNever();
            });
        }
    }
    private sealed class GeneratedRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int NameLength { get; set; }
        public int Version { get; set; }
    }
    private sealed class PlainRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
