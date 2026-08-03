using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;

namespace BlueTusk.Streams;

public sealed class PostgreSqlSnapshotTable
{
    private readonly IReadOnlyList<int> _keyOrdinals;

    public PostgreSqlSnapshotTable(ChangeTable table, IEnumerable<int> keyOrdinals)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        ArgumentNullException.ThrowIfNull(keyOrdinals);
        var keys = keyOrdinals.Distinct().ToArray();
        if (keys.Length == 0)
        {
            throw new ArgumentException("A snapshot table requires at least one key column.", nameof(keyOrdinals));
        }

        foreach (var ordinal in keys)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
            if (ordinal >= table.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyOrdinals),
                    $"Key ordinal {ordinal} is outside relation {table}.");
            }

            if (!table.Columns[ordinal].IsKey)
            {
                throw new ArgumentException(
                    $"Column {table.Columns[ordinal].Name} is not marked as a key column.",
                    nameof(keyOrdinals));
            }
        }

        _keyOrdinals = Array.AsReadOnly(keys);
    }

    public ChangeTable Table { get; }

    public IReadOnlyList<int> KeyOrdinals => _keyOrdinals;
}

public enum PostgreSqlExistingSnapshotSlotMode
{
    Fail,
    RestartSnapshot,
}

public sealed record PostgreSqlConsistentSnapshotOptions
{
    public required ChangeSourceIdentity Source { get; init; }

    public required IReadOnlyList<string> PublicationNames { get; init; }

    public required IReadOnlyList<PostgreSqlSnapshotTable> Tables { get; init; }

    public int CopyPageRows { get; init; } = 2_048;

    public int MaximumBatchRows { get; init; } = 512;

    public long MaximumBatchBytes { get; init; } = 4L * 1024 * 1024;

    public long MaximumRowBytes { get; init; } = 4L * 1024 * 1024;

    public int MaximumParallelTables { get; init; } = 4;

    public PostgreSqlExistingSnapshotSlotMode ExistingSlotMode { get; init; } =
        PostgreSqlExistingSnapshotSlotMode.Fail;

    public TransactionAssemblyOptions TransactionAssembly { get; init; } = new();

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentNullException.ThrowIfNull(PublicationNames);
        ArgumentNullException.ThrowIfNull(Tables);
        if (PublicationNames.Count == 0 || PublicationNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty publication name is required.", nameof(PublicationNames));
        }

        if (Tables.Count == 0)
        {
            throw new ArgumentException("At least one snapshot table is required.", nameof(Tables));
        }

        if (!string.Equals(Source.SlotName, Source.SlotName.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The source slot name cannot have surrounding whitespace.", nameof(Source));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CopyPageRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBatchRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRowBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumParallelTables);
        if (!Enum.IsDefined(ExistingSlotMode))
        {
            throw new ArgumentOutOfRangeException(nameof(ExistingSlotMode));
        }

        if (MaximumRowBytes > MaximumBatchBytes)
        {
            throw new ArgumentException(
                "The maximum individual row byte count cannot exceed the maximum batch byte count.",
                nameof(MaximumRowBytes));
        }

        if (Tables.Select(table => table.Table.ToString()).Distinct(StringComparer.Ordinal).Count() != Tables.Count)
        {
            throw new ArgumentException("Snapshot tables must be unique.", nameof(Tables));
        }

        TransactionAssembly.Validate();
    }
}

public sealed class PostgreSqlConsistentSnapshotSource : IConsistentSnapshotSource
{
    private readonly BlueTuskDataSource _dataSource;
    private readonly PostgreSqlConsistentSnapshotOptions _options;
    private readonly Func<BlueTuskLogicalReplicationConnection, IChangeDeliveryObserver?>? _observerFactory;
    private readonly TimeProvider _timeProvider;
    private int _createdSlot;
    private int _existingSlotChecked;

    public PostgreSqlConsistentSnapshotSource(
        BlueTuskDataSource dataSource,
        PostgreSqlConsistentSnapshotOptions options,
        Func<BlueTuskLogicalReplicationConnection, IChangeDeliveryObserver?>? observerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _observerFactory = observerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
        Guid? abandonedEpoch,
        CancellationToken cancellationToken = default)
    {
        var dedicatedOptions = _dataSource.CreateDedicatedSessionOptions();
        if (abandonedEpoch is null &&
            _options.ExistingSlotMode == PostgreSqlExistingSnapshotSlotMode.RestartSnapshot &&
            Interlocked.Exchange(ref _existingSlotChecked, 1) == 0)
        {
            await RemoveRestartableSlotAsync(dedicatedOptions, cancellationToken).ConfigureAwait(false);
        }

        if (abandonedEpoch is not null)
        {
            if (Volatile.Read(ref _createdSlot) == 0)
            {
                throw new SnapshotAttemptException(
                    "The source cannot prove ownership of the abandoned logical replication slot.");
            }

            await RemoveAbandonedSlotAsync(dedicatedOptions, cancellationToken).ConfigureAwait(false);
        }

        BlueTuskLogicalReplicationConnection? replication = null;
        var slotCreated = false;
        try
        {
            replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
                dedicatedOptions,
                cancellationToken).ConfigureAwait(false);
            var system = await replication.IdentifySystemAsync(cancellationToken).ConfigureAwait(false);
            ValidateIdentity(system);
            var slot = await replication.CreateReplicationSlotAsync(
                new BlueTuskLogicalReplicationSlotCreationOptions
                {
                    SlotName = _options.Source.SlotName,
                    OutputPlugin = "pgoutput",
                    SnapshotMode = BlueTuskLogicalSlotSnapshotMode.Export,
                },
                cancellationToken).ConfigureAwait(false);
            slotCreated = true;
            Volatile.Write(ref _createdSlot, 1);
            if (string.IsNullOrWhiteSpace(slot.SnapshotName))
            {
                throw new SnapshotAttemptException(
                    "PostgreSQL created the logical slot without returning an exported snapshot.");
            }

            var attempt = new PostgreSqlConsistentSnapshotAttempt(
                _dataSource,
                replication,
                slot,
                SnapshotEpoch.Create(_options.Source, slot.ConsistentPoint, _timeProvider),
                _options,
                _observerFactory);
            replication = null;
            return attempt;
        }
        catch (SnapshotAttemptException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SnapshotSessionLostException(
                "PostgreSQL could not establish an exported logical-replication snapshot.",
                exception);
        }
        finally
        {
            if (replication is not null)
            {
                if (slotCreated && replication.IsOpen)
                {
                    try
                    {
                        await replication.DropReplicationSlotAsync(
                            _options.Source.SlotName,
                            wait: true,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The original failure remains authoritative; retry cleanup verifies ownership.
                    }
                }

                await replication.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveAbandonedSlotAsync(
        BlueTuskClientOptions dedicatedOptions,
        CancellationToken cancellationToken)
    {
        await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(
            dedicatedOptions,
            cancellationToken).ConfigureAwait(false);
        var slots = await cleanup.GetReplicationSlotsAsync(cancellationToken).ConfigureAwait(false);
        var existing = slots.SingleOrDefault(slot =>
            string.Equals(slot.SlotName, _options.Source.SlotName, StringComparison.Ordinal));
        if (existing is null)
        {
            return;
        }

        if (existing.IsActive)
        {
            throw new SnapshotAttemptException(
                $"Abandoned slot {_options.Source.SlotName} is active and cannot be replaced safely.");
        }

        await cleanup.DropReplicationSlotAsync(
            _options.Source.SlotName,
            wait: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveRestartableSlotAsync(
        BlueTuskClientOptions dedicatedOptions,
        CancellationToken cancellationToken)
    {
        await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(
            dedicatedOptions,
            cancellationToken).ConfigureAwait(false);
        var slots = await cleanup.GetReplicationSlotsAsync(cancellationToken).ConfigureAwait(false);
        var existing = slots.SingleOrDefault(slot =>
            string.Equals(slot.SlotName, _options.Source.SlotName, StringComparison.Ordinal));
        if (existing is null)
        {
            return;
        }

        if (existing.IsActive ||
            !string.Equals(existing.SlotType, "logical", StringComparison.Ordinal) ||
            !string.Equals(existing.OutputPlugin, "pgoutput", StringComparison.Ordinal) ||
            !string.Equals(existing.DatabaseName, _options.Source.DatabaseName, StringComparison.Ordinal))
        {
            throw new SnapshotAttemptException(
                $"Existing slot {_options.Source.SlotName} is active or does not belong to the configured " +
                "pgoutput snapshot source; it cannot be replaced safely.");
        }

        await cleanup.DropReplicationSlotAsync(
            _options.Source.SlotName,
            wait: true,
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidateIdentity(BlueTuskReplicationSystemIdentity system)
    {
        if (!string.Equals(
                system.SystemIdentifier,
                _options.Source.SystemIdentifier,
                StringComparison.Ordinal) ||
            !string.Equals(
                system.DatabaseName,
                _options.Source.DatabaseName,
                StringComparison.Ordinal))
        {
            throw new SnapshotAttemptException(
                "The connected PostgreSQL system/database identity does not match the configured change source.");
        }
    }
}

internal sealed class PostgreSqlConsistentSnapshotAttempt : IConsistentSnapshotAttempt
{
    private readonly BlueTuskDataSource _dataSource;
    private readonly BlueTuskLogicalReplicationConnection _replication;
    private readonly BlueTuskReplicationSlotCreationResult _slot;
    private readonly PostgreSqlConsistentSnapshotOptions _options;
    private readonly Func<BlueTuskLogicalReplicationConnection, IChangeDeliveryObserver?>? _observerFactory;
    private readonly IReadOnlyList<ChangeTable> _tables;
    private int _snapshotStarted;
    private int _snapshotCompleted;
    private int _streamStarted;
    private int _disposed;

    public PostgreSqlConsistentSnapshotAttempt(
        BlueTuskDataSource dataSource,
        BlueTuskLogicalReplicationConnection replication,
        BlueTuskReplicationSlotCreationResult slot,
        SnapshotEpoch epoch,
        PostgreSqlConsistentSnapshotOptions options,
        Func<BlueTuskLogicalReplicationConnection, IChangeDeliveryObserver?>? observerFactory)
    {
        _dataSource = dataSource;
        _replication = replication;
        _slot = slot;
        Epoch = epoch;
        _options = options;
        _observerFactory = observerFactory;
        _tables = Array.AsReadOnly(options.Tables.Select(table => table.Table).ToArray());
    }

    public SnapshotEpoch Epoch { get; }

    public IReadOnlyList<ChangeTable> Tables => _tables;

    public async IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _snapshotStarted, 1) != 0)
        {
            throw new InvalidOperationException("A consistent snapshot attempt can be read only once.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<ChangeSnapshotBatch>(
            new BoundedChannelOptions(Math.Max(1, _options.MaximumParallelTables * 2))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = _options.MaximumParallelTables == 1,
                AllowSynchronousContinuations = false,
            });
        using var concurrency = new SemaphoreSlim(_options.MaximumParallelTables);
        var producers = _options.Tables
            .Select(table => ProduceTableAsync(
                table,
                channel.Writer,
                concurrency,
                linkedCancellation.Token))
            .ToArray();
        var completion = CompleteProducersAsync(
            producers,
            channel.Writer,
            linkedCancellation.Token);
        try
        {
            await foreach (var batch in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return batch;
            }

            await completion.ConfigureAwait(false);
            Volatile.Write(ref _snapshotCompleted, 1);
        }
        finally
        {
            await linkedCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                // The caller's cancellation remains authoritative.
            }
        }
    }

    public IChangeStream CreateChangeStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _snapshotCompleted) == 0)
        {
            throw new InvalidOperationException("The snapshot must complete before replication starts.");
        }

        if (Interlocked.CompareExchange(ref _streamStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("A consistent snapshot attempt can create only one change stream.");
        }

        try
        {
            var observer = _observerFactory?.Invoke(_replication);
            return new PgOutputChangeStream(
                _replication.StartReplicationAsync(
                    new BlueTuskPgOutputReplicationOptions
                    {
                        SlotName = _options.Source.SlotName,
                        PublicationNames = _options.PublicationNames,
                        StartPosition = Epoch.ConsistentPosition,
                        ProtocolVersion = 2,
                        Messages = true,
                        StreamingMode = BlueTuskLogicalStreamingMode.On,
                    }).DecodePgOutputAsync(),
                _options.Source,
                _options.TransactionAssembly,
                observer: observer);
        }
        catch
        {
            Volatile.Write(ref _streamStarted, 0);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _streamStarted) == 0 && _replication.IsOpen)
        {
            try
            {
                await _replication.DropReplicationSlotAsync(
                    _options.Source.SlotName,
                    wait: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // A later attempt checks and removes the inactive abandoned slot before recreating it.
            }
        }

        await _replication.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProduceTableAsync(
        PostgreSqlSnapshotTable snapshotTable,
        ChannelWriter<ChangeSnapshotBatch> writer,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadTableAsync(snapshotTable, writer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task ReadTableAsync(
        PostgreSqlSnapshotTable snapshotTable,
        ChannelWriter<ChangeSnapshotBatch> writer,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        await using (var import = new BlueTuskCommand(
            $"SET TRANSACTION SNAPSHOT {BlueTuskSql.QuoteLiteral(_slot.SnapshotName!)}",
            connection)
        {
            Transaction = transaction,
        })
        {
            _ = await import.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var lastKeyLiterals = Array.Empty<string>();
        long sequence = 0;
        while (true)
        {
            var query = BuildCopyQuery(snapshotTable, lastKeyLiterals);
            await using var exporter = await connection.BeginBinaryExportAsync(
                query,
                cancellationToken).ConfigureAwait(false);
            var pageRows = 0;
            var batchRows = new List<ChangeSnapshotRow>(_options.MaximumBatchRows);
            long batchBytes = 0;
            while (await exporter.StartRowAsync(cancellationToken).ConfigureAwait(false) != -1)
            {
                pageRows++;
                var values = new ChangeColumnValue[snapshotTable.Table.Columns.Count];
                long rowBytes = 0;
                for (var ordinal = 0; ordinal < values.Length; ordinal++)
                {
                    var payload = await exporter.ReadRawAsync(cancellationToken).ConfigureAwait(false);
                    if (payload is null)
                    {
                        values[ordinal] = ChangeColumnValue.DatabaseNull;
                    }
                    else
                    {
                        rowBytes = checked(rowBytes + payload.Value.Length);
                        values[ordinal] = ChangeColumnValue.FromOwnedValue(
                            payload.Value,
                            ChangeValueEncoding.Binary);
                    }
                }

                var nextKeyLiterals = new string[snapshotTable.KeyOrdinals.Count];
                for (var keyIndex = 0; keyIndex < nextKeyLiterals.Length; keyIndex++)
                {
                    var literal = await exporter.ReadRawAsync(cancellationToken).ConfigureAwait(false);
                    if (literal is null)
                    {
                        throw new SnapshotAttemptException(
                            $"Snapshot key {snapshotTable.Table.Columns[snapshotTable.KeyOrdinals[keyIndex]].Name} is null.");
                    }

                    rowBytes = checked(rowBytes + literal.Value.Length);
                    nextKeyLiterals[keyIndex] = Encoding.UTF8.GetString(literal.Value.Span);
                }

                if (rowBytes > _options.MaximumRowBytes)
                {
                    throw new SnapshotAttemptException(
                        $"Snapshot row in {snapshotTable.Table} uses {rowBytes} bytes; " +
                        $"the configured maximum is {_options.MaximumRowBytes} bytes.");
                }

                if (batchRows.Count > 0 &&
                    (batchRows.Count == _options.MaximumBatchRows ||
                     checked(batchBytes + rowBytes) > _options.MaximumBatchBytes))
                {
                    await writer.WriteAsync(
                        new ChangeSnapshotBatch(
                            Epoch,
                            snapshotTable.Table,
                            sequence++,
                            batchRows,
                            isLastForTable: false),
                        cancellationToken).ConfigureAwait(false);
                    batchRows = new List<ChangeSnapshotRow>(_options.MaximumBatchRows);
                    batchBytes = 0;
                }

                var row = new ChangeRow(snapshotTable.Table, values);
                var keys = snapshotTable.KeyOrdinals.Select(ordinal => values[ordinal]);
                batchRows.Add(new ChangeSnapshotRow(
                    SnapshotRowId.Create(Epoch, snapshotTable.Table, keys),
                    row));
                batchBytes = checked(batchBytes + rowBytes);
                lastKeyLiterals = nextKeyLiterals;
            }

            var isLastPage = pageRows < _options.CopyPageRows;
            if (batchRows.Count > 0 || isLastPage)
            {
                await writer.WriteAsync(
                    new ChangeSnapshotBatch(
                        Epoch,
                        snapshotTable.Table,
                        sequence++,
                        batchRows,
                        isLastPage),
                    cancellationToken).ConfigureAwait(false);
            }

            if (isLastPage)
            {
                break;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildCopyQuery(
        PostgreSqlSnapshotTable snapshotTable,
        string[] lastKeyLiterals)
    {
        var table = snapshotTable.Table;
        var columns = string.Join(
            ", ",
            table.Columns.Select(column => BlueTuskSql.QuoteIdentifier(column.Name)));
        var keys = snapshotTable.KeyOrdinals
            .Select(ordinal => BlueTuskSql.QuoteIdentifier(table.Columns[ordinal].Name))
            .ToArray();
        var keyLiterals = string.Join(
            ", ",
            keys.Select(key => $"pg_catalog.quote_nullable({key})"));
        var where = lastKeyLiterals.Length == 0
            ? string.Empty
            : $" WHERE ROW({string.Join(", ", keys)}) > ROW({string.Join(", ", lastKeyLiterals)})";
        return $"COPY (SELECT {columns}, {keyLiterals} " +
            $"FROM {BlueTuskSql.QuoteIdentifier(table.Schema)}.{BlueTuskSql.QuoteIdentifier(table.Name)}" +
            $"{where} ORDER BY {string.Join(", ", keys)} LIMIT {_options.CopyPageRows}) " +
            "TO STDOUT (FORMAT BINARY)";
    }

    private static async Task CompleteProducersAsync(
        Task[] producers,
        ChannelWriter<ChangeSnapshotBatch> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(producers).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete(new SnapshotSessionLostException(
                "A PostgreSQL snapshot reader stopped unexpectedly.",
                exception));
        }
        catch (OperationCanceledException exception)
        {
            writer.TryComplete(exception);
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception is SnapshotAttemptException
                ? exception
                : new SnapshotSessionLostException(
                    "A PostgreSQL snapshot reader failed before the exported snapshot completed.",
                    exception));
        }
    }
}
