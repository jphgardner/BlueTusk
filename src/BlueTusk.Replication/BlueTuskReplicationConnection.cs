using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;

namespace BlueTusk.Replication;

/// <summary>Common commands and feedback for a PostgreSQL replication connection.</summary>
public abstract class BlueTuskReplicationConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly SemaphoreSlim _feedbackGate = new(1, 1);
    private readonly object _statusSync = new();
    private readonly BlueTuskClientOptions _catalogOptions;
    private BlueTuskCopyBothChannel? _activeChannel;
    private BlueTuskStandbyStatus _standbyStatus;
    private BlueTuskLogSequenceNumber _lastReceivedWalPosition;
    private int _streaming;
    private int _disposed;

    private protected BlueTuskReplicationConnection(
        BlueTuskSession session,
        BlueTuskClientOptions catalogOptions)
    {
        Session = session;
        _catalogOptions = catalogOptions with
        {
            ReplicationMode = BlueTuskReplicationMode.None,
        };
    }

    private protected BlueTuskSession Session { get; }

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Session.IsOpen;

    public bool IsStreaming => Volatile.Read(ref _streaming) != 0;

    public IReadOnlyDictionary<string, string> ServerParameters => Session.Parameters;

    public BlueTuskLogSequenceNumber LastReceivedWalPosition
    {
        get
        {
            lock (_statusSync)
            {
                return _lastReceivedWalPosition;
            }
        }
    }

    public BlueTuskStandbyStatus StandbyStatus
    {
        get
        {
            lock (_statusSync)
            {
                return _standbyStatus;
            }
        }
    }

    /// <summary>Lists physical and logical replication slots visible to the user.</summary>
    public async ValueTask<IReadOnlyList<BlueTuskReplicationSlotInfo>>
        GetReplicationSlotsAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogQueryAsync(
            """
            SELECT
                slot_name,
                plugin,
                slot_type,
                database,
                temporary::text,
                active::text,
                active_pid::text,
                restart_lsn::text,
                confirmed_flush_lsn::text,
                wal_status
            FROM pg_catalog.pg_replication_slots
            ORDER BY slot_name
            """,
            cancellationToken).ConfigureAwait(false);
        var resultSet = GetSingleResultSet(result, "replication slot discovery");
        var slots = new BlueTuskReplicationSlotInfo[resultSet.Rows.Count];
        for (var index = 0; index < slots.Length; index++)
        {
            var row = resultSet.Rows[index];
            slots[index] = new BlueTuskReplicationSlotInfo(
                GetRequiredText(row, 0, "slot_name"),
                GetOptionalText(row, 1),
                GetRequiredText(row, 2, "slot_type"),
                GetOptionalText(row, 3),
                ParseBoolean(GetRequiredText(row, 4, "temporary"), "temporary"),
                ParseBoolean(GetRequiredText(row, 5, "active"), "active"),
                ParseNullableInt32(GetOptionalText(row, 6), "active_pid"),
                ParseNullablePosition(GetOptionalText(row, 7)),
                ParseNullablePosition(GetOptionalText(row, 8)),
                GetOptionalText(row, 9));
        }

        return slots;
    }

    /// <summary>Reads the server identity, timeline, and current WAL position.</summary>
    public async ValueTask<BlueTuskReplicationSystemIdentity> IdentifySystemAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync(
            "IDENTIFY_SYSTEM",
            cancellationToken).ConfigureAwait(false);
        var row = GetSingleRow(result, "IDENTIFY_SYSTEM");
        return new BlueTuskReplicationSystemIdentity(
            GetRequiredText(row, 0, "systemid"),
            ParseUInt32(GetRequiredText(row, 1, "timeline"), "timeline"),
            BlueTuskLogSequenceNumber.Parse(GetRequiredText(row, 2, "xlogpos")),
            GetOptionalText(row, 3));
    }

    /// <summary>Reads one server setting through the replication command protocol.</summary>
    public async ValueTask<string> ShowAsync(
        string parameterName,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync(
            $"SHOW {BlueTuskSql.QuoteIdentifier(parameterName)}",
            cancellationToken).ConfigureAwait(false);
        return GetRequiredText(GetSingleRow(result, "SHOW"), 0, parameterName);
    }

    /// <summary>Sends current write, flush, and apply positions to the WAL sender.</summary>
    public async ValueTask SendStandbyStatusUpdateAsync(
        BlueTuskStandbyStatus status,
        CancellationToken cancellationToken = default)
    {
        await _feedbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = Volatile.Read(ref _activeChannel) ??
                throw new InvalidOperationException(
                    "Standby status can only be sent while replication is streaming.");
            lock (_statusSync)
            {
                ValidateStandbyStatus(_standbyStatus, status);
            }

            await channel.WriteAsync(
                BlueTuskReplicationWireProtocol.EncodeStandbyStatus(
                    status,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            lock (_statusSync)
            {
                _standbyStatus = status;
            }
        }
        finally
        {
            _feedbackGate.Release();
        }
    }

    /// <summary>Sends transaction visibility feedback to a physical WAL sender.</summary>
    public async ValueTask SendHotStandbyFeedbackAsync(
        BlueTuskHotStandbyFeedback feedback,
        CancellationToken cancellationToken = default)
    {
        var channel = Volatile.Read(ref _activeChannel) ??
            throw new InvalidOperationException(
                "Hot standby feedback can only be sent while replication is streaming.");
        await channel.WriteAsync(
            BlueTuskReplicationWireProtocol.EncodeHotStandbyFeedback(
                feedback,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        _disposeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected ValueTask<BlueTuskQueryResult> ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Session.ExecuteSimpleQueryAsync(command, cancellationToken);
    }

    private protected async ValueTask<BlueTuskQueryResult> ExecuteCatalogQueryAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var session = await BlueTuskSession.OpenAsync(
            _catalogOptions,
            cancellationToken).ConfigureAwait(false);
        return await session.ExecuteSimpleQueryAsync(sql, cancellationToken)
            .ConfigureAwait(false);
    }

    private protected async IAsyncEnumerable<BlueTuskReplicationMessage> StreamAsync(
        string command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _streaming, 1) != 0)
        {
            throw new InvalidOperationException(
                "Only one replication stream can be active on a connection.");
        }

        BlueTuskCopyBothChannel? channel = null;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        try
        {
            channel = await Session.BeginCopyBothAsync(
                command,
                linkedCancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref _activeChannel, channel);

            while (true)
            {
                var payload = await channel.ReadAsync(linkedCancellation.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    yield break;
                }

                var message = BlueTuskReplicationWireProtocol.Decode(payload.Value);
                if (message is BlueTuskXLogData xLogData)
                {
                    RecordReceivedPosition(xLogData.WalEnd);
                    RecordReplicationLag(
                        xLogData.ServerClock,
                        xLogData.ServerWalEnd,
                        xLogData.WalEnd);
                }
                else if (message is BlueTuskPrimaryKeepalive keepalive)
                {
                    RecordReplicationLag(
                        keepalive.ServerClock,
                        keepalive.ServerWalEnd,
                        LastReceivedWalPosition);
                    if (keepalive.ReplyRequested)
                    {
                        await ReplyToKeepaliveAsync(channel, linkedCancellation.Token)
                            .ConfigureAwait(false);
                    }
                }

                yield return message;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeChannel, null, channel);
            if (channel is not null)
            {
                if (_disposeCancellation.IsCancellationRequested)
                {
                    await channel.AbortAsync().ConfigureAwait(false);
                }
                else
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }

            Interlocked.Exchange(ref _streaming, 0);
        }
    }

    private async ValueTask ReplyToKeepaliveAsync(
        BlueTuskCopyBothChannel channel,
        CancellationToken cancellationToken)
    {
        await _feedbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BlueTuskStandbyStatus status;
            lock (_statusSync)
            {
                status = _standbyStatus with { ReplyRequested = false };
            }

            await channel.WriteAsync(
                BlueTuskReplicationWireProtocol.EncodeStandbyStatus(
                    status,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _feedbackGate.Release();
        }
    }

    internal static void ValidateStandbyStatus(
        BlueTuskStandbyStatus current,
        BlueTuskStandbyStatus next)
    {
        if (next.Flushed > next.Written)
        {
            throw new ArgumentException(
                "A standby flush position cannot be ahead of its write position.",
                nameof(next));
        }

        if (next.Applied > next.Flushed)
        {
            throw new ArgumentException(
                "A standby apply position cannot be ahead of its flush position.",
                nameof(next));
        }

        if (next.Written < current.Written ||
            next.Flushed < current.Flushed ||
            next.Applied < current.Applied)
        {
            throw new ArgumentException(
                "Standby write, flush, and apply positions cannot move backwards.",
                nameof(next));
        }
    }

    private void RecordReceivedPosition(BlueTuskLogSequenceNumber position)
    {
        lock (_statusSync)
        {
            if (position > _lastReceivedWalPosition)
            {
                _lastReceivedWalPosition = position;
            }
        }
    }

    private void RecordReplicationLag(
        DateTimeOffset serverClock,
        BlueTuskLogSequenceNumber serverWalEnd,
        BlueTuskLogSequenceNumber receivedWalEnd) =>
        BlueTuskDiagnostics.RecordReplicationLag(
            serverClock,
            serverWalEnd.Value,
            receivedWalEnd.Value,
            _catalogOptions.Database,
            _catalogOptions.Host,
            _catalogOptions.Port);

    private protected static BlueTuskDataRow GetSingleRow(
        BlueTuskQueryResult result,
        string command)
    {
        var resultSet = GetSingleResultSet(result, command);
        return resultSet.Rows.Count == 1
            ? resultSet.Rows[0]
            : throw new BlueTuskReplicationProtocolException(
                $"{command} returned {resultSet.Rows.Count} rows instead of one.");
    }

    private protected static BlueTuskResultSet GetSingleResultSet(
        BlueTuskQueryResult result,
        string command) =>
        result.ResultSets.Count == 1
            ? result.ResultSets[0]
            : throw new BlueTuskReplicationProtocolException(
                $"{command} returned {result.ResultSets.Count} result sets instead of one.");

    private protected static string GetRequiredText(
        BlueTuskDataRow row,
        int index,
        string fieldName) =>
        GetOptionalText(row, index) ??
        throw new BlueTuskReplicationProtocolException(
            $"The {fieldName} replication field was null.");

    private protected static string? GetOptionalText(BlueTuskDataRow row, int index)
    {
        if ((uint)index >= (uint)row.Values.Count)
        {
            throw new BlueTuskReplicationProtocolException(
                $"A replication response did not contain field index {index}.");
        }

        return row.Values[index] is { } value
            ? Encoding.UTF8.GetString(value.Span)
            : null;
    }

    private protected static uint ParseUInt32(string value, string fieldName) =>
        uint.TryParse(value, out var parsed)
            ? parsed
            : throw new BlueTuskReplicationProtocolException(
                $"The {fieldName} replication field was not an unsigned integer.");

    private protected static bool ParseBoolean(string value, string fieldName) =>
        value switch
        {
            "t" or "true" => true,
            "f" or "false" => false,
            _ => throw new BlueTuskReplicationProtocolException(
                $"The {fieldName} catalog field was not a PostgreSQL boolean."),
        };

    private static int? ParseNullableInt32(string? value, string fieldName) =>
        value is null
            ? null
            : int.TryParse(value, out var parsed)
                ? parsed
                : throw new BlueTuskReplicationProtocolException(
                    $"The {fieldName} catalog field was not a 32-bit integer.");

    private static BlueTuskLogSequenceNumber? ParseNullablePosition(string? value) =>
        value is null
            ? null
            : BlueTuskLogSequenceNumber.Parse(value);
}
