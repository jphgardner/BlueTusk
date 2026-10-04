using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.DependencyInjection;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

/// <summary>
/// A consumer registered through <see cref="BlueTuskStreamsBuilder"/> with a stream factory resumes
/// from its durable checkpoint after the host restarts, and its checkpointing observer confirms
/// each acknowledged commit to PostgreSQL. The slot lives in a database that the concurrently
/// running test classes never write to (see <see cref="LogicalSlotDatabaseFixture"/>); the
/// checkpoint store lives in the shared test database, outside the slot's database.
/// </summary>
public sealed class BlueTuskStreamsResumingConsumerIntegrationTests(LogicalSlotDatabaseFixture database)
    : IClassFixture<LogicalSlotDatabaseFixture>
{
    private const string ConsumerGroup = "orders";
    private const string MappingFingerprint = "orders-v1";
    private static readonly TimeSpan ProgressTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Hosted_consumer_resumes_from_its_checkpoint_after_restart_and_confirms_progress()
    {
        var sharedConnectionString = GetSharedConnectionString();
        var connectionString = await database.GetConnectionStringAsync(sharedConnectionString);
        await using var fixture = await ResumeFixture.CreateAsync(connectionString, sharedConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var created = await fixture.ReadSlotAsync();

            // First run: no checkpoint yet, so the stream starts at the slot's consistent point.
            BlueTuskLogSequenceNumber firstCommit;
            await using (var first = await fixture.StartHostAsync())
            {
                await fixture.InsertAsync(1);
                var delivered = await first.Consumer.ReadChangedAsync(timeout.Token);
                Assert.Equal([1], delivered.Ids);
                firstCommit = delivered.CommitEnd;

                var confirmed = await fixture.WaitForSlotAsync(
                    slot => slot.ConfirmedFlush >= firstCommit,
                    $"confirmed_flush_lsn to reach the acknowledged commit {firstCommit}",
                    timeout.Token);
                Assert.True(confirmed.ConfirmedFlush > created.ConfirmedFlush);
                Assert.Equal(firstCommit, (await fixture.ReadCheckpointAsync())!.AcknowledgedCommitPosition);
                Assert.Equal(BlueTuskStreamWorkerState.Running, first.Status().State);

                await first.StopAsync();
                Assert.Equal(BlueTuskStreamWorkerState.Stopped, first.Status().State);
                Assert.Equal(1, first.Opened);
                Assert.Equal(1, first.Released);
            }

            // Written while no consumer is running; the restarted consumer must deliver it.
            await fixture.InsertAsync(2);
            await fixture.WaitForSlotAsync(
                slot => !slot.Active,
                "the stopped worker to release the replication slot",
                timeout.Token);

            await using (var second = await fixture.StartHostAsync())
            {
                var resumed = await second.Consumer.ReadChangedAsync(timeout.Token);
                Assert.Equal([2], resumed.Ids);
                Assert.True(resumed.CommitEnd > firstCommit);

                await fixture.InsertAsync(3);
                var next = await second.Consumer.ReadChangedAsync(timeout.Token);
                Assert.Equal([3], next.Ids);

                var confirmed = await fixture.WaitForSlotAsync(
                    slot => slot.ConfirmedFlush >= next.CommitEnd,
                    $"confirmed_flush_lsn to reach the resumed commit {next.CommitEnd}",
                    timeout.Token);
                Assert.True(confirmed.ConfirmedFlush > firstCommit);
                var checkpoint = await fixture.ReadCheckpointAsync();
                Assert.Equal(next.CommitEnd, checkpoint!.AcknowledgedCommitPosition);
                Assert.True(checkpoint.StoreGeneration >= 2, $"generation {checkpoint.StoreGeneration}");

                await second.StopAsync();
                Assert.Equal(BlueTuskStreamWorkerState.Stopped, second.Status().State);
                Assert.Equal(1, second.Released);
                Assert.Equal([2, 3], second.Consumer.DeliveredIds);
            }
        }
        finally
        {
            await fixture.DropSlotAsync();
        }
    }

    // The registration under test, written the way the Streams hosting guide shows it: an async
    // iterator that owns the replication connection and the checkpointing observer.
    private static async IAsyncEnumerable<ChangeTransactionDelivery> ResumeFromCheckpointAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<ResumeSettings>();
        var store = services.GetRequiredService<IChangeStreamStateStore>();
        settings.Opened();
        try
        {
            await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
                settings.ConnectionString,
                cancellationToken);
            var system = await replication.IdentifySystemAsync(cancellationToken);
            var source = new ChangeSourceIdentity(
                system.SystemIdentifier,
                system.DatabaseName!,
                settings.SlotName,
                settings.PublicationName);
            await using var observer = await CheckpointingChangeDeliveryObserver.AcquireAsync(
                store,
                ChangeStreamStateKey.Create(source, ConsumerGroup),
                settings.OwnerId,
                TimeSpan.FromSeconds(30),
                ChangeStreamCheckpoint.CreateInitial(source, system.DatabaseName!, "pgoutput", MappingFingerprint),
                new LogicalReplicationFeedbackSender(replication),
                cancellationToken);

            var start = BlueTuskLogSequenceNumber.Zero;
            if (observer.Checkpoint is { } checkpoint)
            {
                start = checkpoint.AcknowledgedCommitPosition;
                _ = await replication.ValidateResumeCheckpointAsync(
                    new BlueTuskLogicalReplicationCheckpoint(
                        system.SystemIdentifier,
                        system.DatabaseName!,
                        settings.SlotName,
                        "pgoutput",
                        start),
                    cancellationToken);
            }

            var stream = new PgOutputChangeStream(
                replication.StartReplicationAsync(
                        new BlueTuskPgOutputReplicationOptions
                        {
                            SlotName = settings.SlotName,
                            PublicationNames = [settings.PublicationName],
                            StartPosition = start,
                        },
                        cancellationToken)
                    .DecodePgOutputAsync(cancellationToken: cancellationToken),
                source,
                observer: observer);
            await foreach (var delivery in stream.ReadTransactionsAsync(cancellationToken))
            {
                yield return delivery;
            }
        }
        finally
        {
            settings.Released();
        }
    }

    private static string GetSharedConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }

    private readonly record struct SlotState(
        BlueTuskLogSequenceNumber ConfirmedFlush,
        bool Active);

    private sealed class ResumeSettings(
        string connectionString,
        string slotName,
        string publicationName)
    {
        private int _opened;
        private int _released;

        public string ConnectionString { get; } = connectionString;

        public string SlotName { get; } = slotName;

        public string PublicationName { get; } = publicationName;

        public string OwnerId { get; } = "worker-" + Guid.NewGuid().ToString("N");

        public int OpenedCount => Volatile.Read(ref _opened);

        public int ReleasedCount => Volatile.Read(ref _released);

        public void Opened() => Interlocked.Increment(ref _opened);

        public void Released() => Interlocked.Increment(ref _released);
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IHostedService _hosted;
        private readonly ResumeSettings _settings;
        private bool _stopped;

        public RunningHost(ServiceProvider provider, IHostedService hosted, ResumeSettings settings)
        {
            _provider = provider;
            _hosted = hosted;
            _settings = settings;
        }

        public RecordingConsumer Consumer => _provider.GetRequiredService<RecordingConsumer>();

        public int Opened => _settings.OpenedCount;

        public int Released => _settings.ReleasedCount;

        public BlueTuskStreamWorkerStatus Status() =>
            Assert.Single(_provider.GetRequiredService<BlueTuskStreamHealthRegistry>().GetStatuses());

        public async Task StopAsync()
        {
            _stopped = true;
            await _hosted.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                await StopAsync();
            }

            await _provider.DisposeAsync();
        }
    }

    private sealed class ResumeFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly BlueTuskConnection _administration;
        private readonly BlueTuskDataSource _controlDataSource;
        private readonly PostgreSqlChangeStreamStateStore _store;
        private readonly ChangeStreamStateKey _key;

        private ResumeFixture(
            string connectionString,
            BlueTuskConnection administration,
            BlueTuskDataSource controlDataSource,
            string controlSchema,
            string suffix,
            ChangeSourceIdentity source)
        {
            _connectionString = connectionString;
            _administration = administration;
            _controlDataSource = controlDataSource;
            ControlSchema = controlSchema;
            TableName = $"bluetusk_resume_{suffix}";
            PublicationName = $"bluetusk_resume_publication_{suffix}";
            SlotName = source.SlotName;
            QuotedTable = BlueTuskSql.QuoteIdentifier(TableName);
            _store = new PostgreSqlChangeStreamStateStore(new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = controlDataSource,
                ControlSchema = controlSchema,
            });
            _key = ChangeStreamStateKey.Create(source, ConsumerGroup);
        }

        public string ControlSchema { get; }

        public string TableName { get; }

        public string PublicationName { get; }

        public string SlotName { get; }

        public string QuotedTable { get; }

        public static async Task<ResumeFixture> CreateAsync(
            string connectionString,
            string controlConnectionString)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var slotName = $"bt_resume_slot_{suffix}";
            var publicationName = $"bluetusk_resume_publication_{suffix}";
            var administration = new BlueTuskConnection(connectionString);
            await administration.OpenAsync(CancellationToken.None);
            BlueTuskReplicationSystemIdentity system;
            await using (var identity = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                system = await identity.IdentifySystemAsync();
            }

            var fixture = new ResumeFixture(
                connectionString,
                administration,
                BlueTuskDataSource.Create(controlConnectionString),
                "bluetusk_streams_resume_" + suffix,
                suffix,
                new ChangeSourceIdentity(
                    system.SystemIdentifier,
                    system.DatabaseName!,
                    slotName,
                    publicationName));
            await fixture.ExecuteAsync($"CREATE TABLE {fixture.QuotedTable} (id integer PRIMARY KEY)");
            await fixture.ExecuteAsync(
                $"CREATE PUBLICATION {BlueTuskSql.QuoteIdentifier(publicationName)} " +
                $"FOR TABLE {fixture.QuotedTable}");
            await using (var setup = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                _ = await setup.CreateReplicationSlotAsync(slotName, temporary: false);
            }

            await fixture._store.InitializeAsync();
            return fixture;
        }

        public async Task<RunningHost> StartHostAsync()
        {
            var settings = new ResumeSettings(_connectionString, SlotName, PublicationName);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(settings);
            services.AddSingleton<IChangeStreamStateStore>(_store);
            services.AddSingleton<RecordingConsumer>();
            services
                .AddBlueTuskStreams()
                .AddHostedConsumer<RecordingConsumer>("orders", ResumeFromCheckpointAsync);
            var provider = services.BuildServiceProvider();
            var hosted = Assert.Single(
                provider.GetServices<IHostedService>(),
                service => service.GetType().Name == "BlueTuskStreamsHostedService");
            await hosted.StartAsync(CancellationToken.None);
            return new RunningHost(provider, hosted, settings);
        }

        public Task InsertAsync(int id) =>
            ExecuteAsync($"INSERT INTO {QuotedTable} VALUES ({id.ToString(CultureInfo.InvariantCulture)})");

        public ValueTask<ChangeStreamCheckpoint?> ReadCheckpointAsync() => _store.ReadAsync(_key);

        public async Task<SlotState> ReadSlotAsync()
        {
            await using var command = new BlueTuskCommand(
                "SELECT confirmed_flush_lsn::text, active " +
                $"FROM pg_catalog.pg_replication_slots WHERE slot_name = {BlueTuskSql.QuoteLiteral(SlotName)}",
                _administration);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            if (!await reader.ReadAsync(CancellationToken.None))
            {
                throw new XunitException($"Replication slot {SlotName} does not exist.");
            }

            return new SlotState(
                BlueTuskLogSequenceNumber.Parse(reader.GetString(0)),
                reader.GetBoolean(1));
        }

        public async Task<SlotState> WaitForSlotAsync(
            Func<SlotState, bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(ProgressTimeout);
            var last = await ReadSlotAsync();
            while (!predicate(last))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), wait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new XunitException(
                        $"Timed out waiting for {expectation}; slot has confirmed_flush_lsn " +
                        $"{last.ConfirmedFlush} and active={last.Active}.");
                }

                last = await ReadSlotAsync();
            }

            return last;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = new BlueTuskCommand(sql, _administration);
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async Task DropSlotAsync()
        {
            await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(_connectionString);
            var slots = await cleanup.GetReplicationSlotsAsync();
            if (slots.Any(slot => slot.SlotName == SlotName))
            {
                await cleanup.DropReplicationSlotAsync(SlotName, wait: true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ExecuteAsync(
                    $"DROP PUBLICATION IF EXISTS {BlueTuskSql.QuoteIdentifier(PublicationName)}");
                await ExecuteAsync($"DROP TABLE IF EXISTS {QuotedTable}");
                await using var control = await _controlDataSource.OpenConnectionAsync();
                await using var command = control.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS {BlueTuskSql.QuoteIdentifier(ControlSchema)} CASCADE";
                _ = await command.ExecuteNonQueryAsync();
            }
            finally
            {
                await _controlDataSource.DisposeAsync();
                await _administration.DisposeAsync();
            }
        }
    }

    private sealed class RecordingConsumer : IChangeStreamConsumer
    {
        private readonly List<int> _deliveredIds = [];

        public Channel<(BlueTuskLogSequenceNumber CommitEnd, IReadOnlyList<int> Ids)> Changed { get; } =
            Channel.CreateUnbounded<(BlueTuskLogSequenceNumber, IReadOnlyList<int>)>();

        public IReadOnlyList<int> DeliveredIds
        {
            get
            {
                lock (_deliveredIds)
                {
                    return _deliveredIds.ToArray();
                }
            }
        }

        public async Task<(BlueTuskLogSequenceNumber CommitEnd, IReadOnlyList<int> Ids)> ReadChangedAsync(
            CancellationToken cancellationToken) =>
            await Changed.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(ProgressTimeout, cancellationToken);

        public ValueTask ResetSnapshotAsync(SnapshotReset reset, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A resuming consumer must not receive a snapshot.");

        public ValueTask StartSnapshotAsync(SnapshotStart start, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A resuming consumer must not receive a snapshot.");

        public ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A resuming consumer must not receive a snapshot.");

        public ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A resuming consumer must not receive a snapshot.");

        public async ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            var changes = await delivery.Transaction.Changes.MaterializeAsync(cancellationToken);
            var ids = changes
                .OfType<InsertChange>()
                .Select(change => int.Parse(
                    Encoding.UTF8.GetString(change.NewRow["id"].Data.Span),
                    CultureInfo.InvariantCulture))
                .ToArray();
            await delivery.AcknowledgeAsync(cancellationToken);
            if (ids.Length > 0)
            {
                lock (_deliveredIds)
                {
                    _deliveredIds.AddRange(ids);
                }

                Changed.Writer.TryWrite((delivery.Transaction.CommitEndPosition, ids));
            }
        }
    }
}
