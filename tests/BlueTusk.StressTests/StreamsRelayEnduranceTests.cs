using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.StressTests;

public sealed class StreamsRelayEnduranceTests
{
    private static readonly JsonSerializerOptions ReportSerializerOptions =
        new() { WriteIndented = true };

    [Fact]
    public async Task Relay_survives_fault_injected_endurance_without_loss_or_unbounded_storage()
    {
        var settings = ReadSettings();
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_endurance_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var storageOptions = new PostgreSqlStreamsStorageOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = schema,
            ResumeRetentionWindow = TimeSpan.Zero,
            RemovedConsumerGroupRetentionWindow = TimeSpan.Zero,
            RetentionDeleteBatchSize = 1024,
            MaxCompactionBatches = 32,
            MaxEnvelopeBytes = 1024 * 1024,
            MaxRelayStorageBytes = 64L * 1024 * 1024,
            MaxAcknowledgementAge = TimeSpan.FromMinutes(10),
        };
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        long transactions = 0;
        long duplicateAppends = 0;
        long replayedDeliveries = 0;
        long generationConflicts = 0;
        long fencedLeases = 0;
        long relayRestarts = 0;
        long maxStorageBytes = 0;
        long maxStoredTransactions = 0;
        try
        {
            var relay = new PostgreSqlDurableChangeRelay(storageOptions);
            await relay.InitializeAsync(TestContext.Current.CancellationToken);
            var identity = new ChangeSourceIdentity(
                "endurance-system",
                "bluetusk_tests",
                "relay_endurance_slot",
                "public:endurance_rows");
            var source = await relay.RegisterSourceAsync(
                identity,
                cancellationToken: TestContext.Current.CancellationToken);
            var sourceLease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    source,
                    "source-0",
                    TimeSpan.FromMinutes(10),
                    TestContext.Current.CancellationToken)).Lease);
            var firstGroup = await relay.CreateConsumerGroupAsync(
                source,
                "sync-endurance",
                cancellationToken: TestContext.Current.CancellationToken);
            var secondGroup = await relay.CreateConsumerGroupAsync(
                source,
                "live-endurance",
                cancellationToken: TestContext.Current.CancellationToken);
            var firstLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    firstGroup,
                    "sync-0",
                    TimeSpan.FromMinutes(10),
                    TestContext.Current.CancellationToken));
            var secondLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    secondGroup,
                    "live-0",
                    TimeSpan.FromMinutes(10),
                    TestContext.Current.CancellationToken));

            while (stopwatch.Elapsed < settings.Duration || transactions == 0)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var transactionId = checked((uint)(transactions + 1));
                var stream = CreateTransactionStream(identity, transactionId);
                await using var enumerator = stream.ReadTransactionsAsync(
                        TestContext.Current.CancellationToken)
                    .GetAsyncEnumerator(TestContext.Current.CancellationToken);
                Assert.True(await enumerator.MoveNextAsync());
                var delivery = enumerator.Current;
                var appended = await relay.AppendAsync(
                    source,
                    delivery.Transaction,
                    sourceLease,
                    TestContext.Current.CancellationToken);
                Assert.Equal(ChangeRelayAppendStatus.Appended, appended.Status);
                transactions++;

                if (transactions % 7 == 0)
                {
                    var duplicate = await relay.AppendAsync(
                        source,
                        delivery.Transaction,
                        sourceLease,
                        TestContext.Current.CancellationToken);
                    Assert.Equal(ChangeRelayAppendStatus.AlreadyPresent, duplicate.Status);
                    duplicateAppends++;
                }

                (firstGroup, replayedDeliveries, generationConflicts) = await ConsumeAsync(
                    relay,
                    firstGroup,
                    firstLease,
                    appended.Sequence,
                    transactions,
                    replayedDeliveries,
                    generationConflicts,
                    TestContext.Current.CancellationToken);
                (secondGroup, replayedDeliveries, generationConflicts) = await ConsumeAsync(
                    relay,
                    secondGroup,
                    secondLease,
                    appended.Sequence,
                    transactions,
                    replayedDeliveries,
                    generationConflicts,
                    TestContext.Current.CancellationToken);
                await delivery.AcknowledgeAsync(TestContext.Current.CancellationToken);

                if (transactions % 101 == 0)
                {
                    var staleLease = firstLease;
                    Assert.True(await relay.ReleaseConsumerGroupAsync(
                        staleLease,
                        TestContext.Current.CancellationToken));
                    firstLease = Assert.IsType<ChangeRelayGroupLease>(
                        await relay.AcquireConsumerGroupAsync(
                            firstGroup,
                            "sync-" + transactions.ToString(CultureInfo.InvariantCulture),
                            TimeSpan.FromMinutes(10),
                            TestContext.Current.CancellationToken));
                    Assert.True(firstLease.FencingToken > staleLease.FencingToken);
                    await Assert.ThrowsAsync<ChangeRelayLeaseLostException>(
                        () => relay.ReadConsumerGroupAsync(
                            staleLease,
                            1,
                            1024 * 1024,
                            TestContext.Current.CancellationToken).AsTask());
                    fencedLeases++;
                }

                if (transactions % 257 == 0)
                {
                    Assert.True(await relay.ReleaseSourceLeaseAsync(
                        sourceLease,
                        TestContext.Current.CancellationToken));
                    sourceLease = Assert.IsType<ChangeStreamLease>(
                        (await relay.AcquireSourceLeaseAsync(
                            source,
                            "source-" + transactions.ToString(CultureInfo.InvariantCulture),
                            TimeSpan.FromMinutes(10),
                            TestContext.Current.CancellationToken)).Lease);
                    relay = new PostgreSqlDurableChangeRelay(storageOptions);
                    await relay.InitializeAsync(TestContext.Current.CancellationToken);
                    relayRestarts++;
                }
                else
                {
                    sourceLease = await RenewSourceLeaseIfNeededAsync(
                        relay,
                        sourceLease,
                        TestContext.Current.CancellationToken);
                }

                firstLease = await RenewGroupLeaseIfNeededAsync(
                    relay,
                    firstLease,
                    TestContext.Current.CancellationToken);
                secondLease = await RenewGroupLeaseIfNeededAsync(
                    relay,
                    secondLease,
                    TestContext.Current.CancellationToken);
                var metrics = await relay.GetMetricsAsync(source, TestContext.Current.CancellationToken);
                maxStorageBytes = Math.Max(maxStorageBytes, metrics.StorageBytes);
                maxStoredTransactions = Math.Max(maxStoredTransactions, metrics.TransactionCount);
                Assert.True(metrics.StorageBytes <= storageOptions.MaxRelayStorageBytes);
                _ = await relay.ApplyRetentionAsync(source, TestContext.Current.CancellationToken);

                if (settings.Interval > TimeSpan.Zero)
                {
                    await Task.Delay(settings.Interval, TestContext.Current.CancellationToken);
                }
            }

            var finalCompaction = await relay.CompactAsync(
                source,
                vacuum: false,
                TestContext.Current.CancellationToken);
            Assert.True(finalCompaction.FullyApplied);
            var finalMetrics = await relay.GetMetricsAsync(source, TestContext.Current.CancellationToken);
            Assert.Equal(0, finalMetrics.TransactionCount);
            Assert.Equal(0, finalMetrics.StorageBytes);
            Assert.Equal(transactions, firstGroup.CheckpointSequence);
            Assert.Equal(transactions, secondGroup.CheckpointSequence);
            Assert.True(transactions >= settings.MinimumTransactions);

            await WriteReportAsync(
                settings.ReportPath,
                new RelayEnduranceReport(
                    StartedAt: startedAt,
                    CompletedAt: DateTimeOffset.UtcNow,
                    RequestedDuration: settings.Duration,
                    ActualDuration: stopwatch.Elapsed,
                    Transactions: transactions,
                    DuplicateAppends: duplicateAppends,
                    ReplayedDeliveries: replayedDeliveries,
                    GenerationConflicts: generationConflicts,
                    FencedLeases: fencedLeases,
                    RelayRestarts: relayRestarts,
                    MaximumStorageBytes: maxStorageBytes,
                    MaximumStoredTransactions: maxStoredTransactions,
                    FinalStorageBytes: finalMetrics.StorageBytes),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    private static async ValueTask<(
        ChangeRelayConsumerGroup Group,
        long ReplayedDeliveries,
        long GenerationConflicts)> ConsumeAsync(
        PostgreSqlDurableChangeRelay relay,
        ChangeRelayConsumerGroup group,
        ChangeRelayGroupLease lease,
        long expectedSequence,
        long transactionNumber,
        long replayedDeliveries,
        long generationConflicts,
        CancellationToken cancellationToken)
    {
        var batch = await relay.ReadConsumerGroupAsync(
            lease,
            16,
            1024 * 1024,
            cancellationToken);
        var record = Assert.Single(batch.Records);
        Assert.Equal(expectedSequence, record.Sequence);
        Assert.Equal(checked((uint)transactionNumber), record.Transaction.TransactionId);
        if (transactionNumber % 11 == 0)
        {
            var replay = await relay.ReadConsumerGroupAsync(
                lease,
                16,
                1024 * 1024,
                cancellationToken);
            Assert.Equal(record.Sequence, Assert.Single(replay.Records).Sequence);
            replayedDeliveries++;
        }

        var priorGeneration = group.StoreGeneration;
        var acknowledged = await relay.AcknowledgeConsumerGroupAsync(
            lease,
            priorGeneration,
            record.Sequence,
            cancellationToken);
        Assert.Equal(ChangeRelayAcknowledgeStatus.Stored, acknowledged.Status);
        group = acknowledged.Current;
        if (transactionNumber % 13 == 0)
        {
            var conflict = await relay.AcknowledgeConsumerGroupAsync(
                lease,
                priorGeneration,
                record.Sequence,
                cancellationToken);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Conflict, conflict.Status);
            generationConflicts++;
        }

        return (group, replayedDeliveries, generationConflicts);
    }

    private static async ValueTask<ChangeStreamLease> RenewSourceLeaseIfNeededAsync(
        PostgreSqlDurableChangeRelay relay,
        ChangeStreamLease lease,
        CancellationToken cancellationToken)
    {
        if (lease.ExpiresAt - DateTimeOffset.UtcNow >= TimeSpan.FromMinutes(2))
        {
            return lease;
        }

        return Assert.IsType<ChangeStreamLease>(
            await relay.RenewSourceLeaseAsync(lease, TimeSpan.FromMinutes(10), cancellationToken));
    }

    private static async ValueTask<ChangeRelayGroupLease> RenewGroupLeaseIfNeededAsync(
        PostgreSqlDurableChangeRelay relay,
        ChangeRelayGroupLease lease,
        CancellationToken cancellationToken)
    {
        if (lease.ExpiresAt - DateTimeOffset.UtcNow >= TimeSpan.FromMinutes(2))
        {
            return lease;
        }

        return Assert.IsType<ChangeRelayGroupLease>(
            await relay.RenewConsumerGroupAsync(lease, TimeSpan.FromMinutes(10), cancellationToken));
    }

    private static PgOutputChangeStream CreateTransactionStream(
        ChangeSourceIdentity source,
        uint transactionId)
    {
        var basePosition = checked((ulong)transactionId * 10);
        var timestamp = DateTimeOffset.UtcNow;
        return new PgOutputChangeStream(
            Messages(
                Envelope(new BlueTuskPgOutputRelation(
                    null,
                    7,
                    "public",
                    "endurance_rows",
                    'd',
                    [new BlueTuskPgOutputRelationColumn(
                        BlueTuskPgOutputRelationColumnOptions.Key,
                        "id",
                        23,
                        -1)]), basePosition, timestamp),
                Envelope(new BlueTuskPgOutputBegin(
                    Lsn(basePosition + 1),
                    timestamp,
                    transactionId), basePosition, timestamp),
                Envelope(new BlueTuskPgOutputInsert(
                    null,
                    7,
                    new BlueTuskPgOutputTuple(
                        [new BlueTuskPgOutputTupleValue(
                            BlueTuskPgOutputTupleValueKind.Text,
                            Encoding.UTF8.GetBytes(
                                transactionId.ToString(CultureInfo.InvariantCulture)))])),
                    basePosition,
                    timestamp),
                Envelope(new BlueTuskPgOutputCommit(
                    Lsn(basePosition + 2),
                    Lsn(basePosition + 3),
                    timestamp), basePosition, timestamp)),
            source);
    }

    private static BlueTuskPgOutputEnvelope Envelope(
        BlueTuskPgOutputMessage message,
        ulong basePosition,
        DateTimeOffset timestamp) =>
        new(
            new BlueTuskXLogData(
                Lsn(basePosition),
                Lsn(basePosition + 4),
                timestamp,
                ReadOnlyMemory<byte>.Empty),
            message);

    private static async IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        IEnumerable<BlueTuskPgOutputEnvelope> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    private static IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        params BlueTuskPgOutputEnvelope[] messages) =>
        Messages((IEnumerable<BlueTuskPgOutputEnvelope>)messages);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private static EnduranceSettings ReadSettings()
    {
        var rawDuration = Environment.GetEnvironmentVariable("BLUETUSK_RELAY_ENDURANCE_DURATION");
        if (!TimeSpan.TryParse(rawDuration, CultureInfo.InvariantCulture, out var duration) ||
            duration < TimeSpan.FromSeconds(1))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_RELAY_ENDURANCE_DURATION is not configured to at least one second.");
        }

        var rawInterval = Environment.GetEnvironmentVariable("BLUETUSK_RELAY_ENDURANCE_INTERVAL_MS");
        var intervalMilliseconds = string.IsNullOrWhiteSpace(rawInterval)
            ? 250
            : int.Parse(rawInterval, NumberStyles.None, CultureInfo.InvariantCulture);
        if (intervalMilliseconds is < 0 or > 60_000)
        {
            throw new InvalidOperationException(
                "BLUETUSK_RELAY_ENDURANCE_INTERVAL_MS must be between 0 and 60000.");
        }

        var rawMinimum = Environment.GetEnvironmentVariable("BLUETUSK_RELAY_ENDURANCE_MIN_TRANSACTIONS");
        var minimumTransactions = string.IsNullOrWhiteSpace(rawMinimum)
            ? 1L
            : long.Parse(rawMinimum, NumberStyles.None, CultureInfo.InvariantCulture);
        if (minimumTransactions <= 0)
        {
            throw new InvalidOperationException(
                "BLUETUSK_RELAY_ENDURANCE_MIN_TRANSACTIONS must be positive.");
        }

        return new EnduranceSettings(
            duration,
            TimeSpan.FromMilliseconds(intervalMilliseconds),
            minimumTransactions,
            Environment.GetEnvironmentVariable("BLUETUSK_RELAY_ENDURANCE_REPORT"));
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static async ValueTask WriteReportAsync(
        string? path,
        RelayEnduranceReport report,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, FindRepositoryRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            ReportSerializerOptions,
            cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the BlueTusk repository root.");
    }

    private sealed record EnduranceSettings(
        TimeSpan Duration,
        TimeSpan Interval,
        long MinimumTransactions,
        string? ReportPath);

    private sealed record RelayEnduranceReport(
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan RequestedDuration,
        TimeSpan ActualDuration,
        long Transactions,
        long DuplicateAppends,
        long ReplayedDeliveries,
        long GenerationConflicts,
        long FencedLeases,
        long RelayRestarts,
        long MaximumStorageBytes,
        long MaximumStoredTransactions,
        long FinalStorageBytes);
}
