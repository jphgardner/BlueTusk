using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.Security;

namespace BlueTusk.Data;

internal interface IBlueTuskPhysicalSession : IDisposable, IAsyncDisposable
{
    bool IsOpen { get; }

    BlueTuskHostEndpoint Endpoint { get; }

    bool? IsPrimary { get; }

    bool? IsReadOnly { get; }

    IReadOnlyDictionary<string, string> Parameters { get; }

    BlueTuskTransactionStatus TransactionStatus { get; }

    ValueTask RefreshHostStateAsync(CancellationToken cancellationToken = default);

    ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
        string sql,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default);

    ValueTask PrepareStatementAsync(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskQueryResult> ExecutePreparedStatementAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default);

    ValueTask ClosePreparedStatementAsync(
        string statementName,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
        IReadOnlyList<BlueTuskBatchQuery> queries,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
        IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskCopyResult> CopyInAsync(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskCopyResult> CopyOutAsync(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskNotificationResponse> WaitForNotificationAsync(
        CancellationToken cancellationToken = default);

    void Cancel();

    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

internal sealed class BlueTuskPhysicalSession : IBlueTuskPhysicalSession
{
    private readonly BlueTuskSession _session;
    private readonly BlueTuskHostEndpoint _endpoint;
    private readonly int _maximumAutoPreparedStatements;
    private readonly int _autoPrepareMinimumUsages;
    private readonly Dictionary<string, AutoPrepareEntry> _autoPrepareEntries =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _autoPrepareGate = new(1, 1);
    private long _autoPrepareClock;
    private long _autoPrepareNameSequence;
    private bool? _isPrimary;
    private bool? _isReadOnly;

    private BlueTuskPhysicalSession(
        BlueTuskSession session,
        BlueTuskHostEndpoint endpoint,
        int maximumAutoPreparedStatements,
        int autoPrepareMinimumUsages)
    {
        _session = session;
        _endpoint = endpoint;
        _maximumAutoPreparedStatements = maximumAutoPreparedStatements;
        _autoPrepareMinimumUsages = autoPrepareMinimumUsages;
    }

    public bool IsOpen => _session.IsOpen;

    public BlueTuskHostEndpoint Endpoint => _endpoint;

    public bool? IsPrimary => _isPrimary;

    public bool? IsReadOnly => _isReadOnly;

    public IReadOnlyDictionary<string, string> Parameters => _session.Parameters;

    public BlueTuskTransactionStatus TransactionStatus => _session.TransactionStatus;

    public static async ValueTask<IBlueTuskPhysicalSession> OpenAsync(
        BlueTuskConnectionStringBuilder settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var endpoints = settings.HostEndpoints.ToArray();
        if (settings.LoadBalanceHosts == BlueTuskLoadBalanceHosts.Random)
        {
            Random.Shared.Shuffle(endpoints);
        }

        var target = settings.TargetSessionAttributes;
        var requiredTarget = target switch
        {
            BlueTuskTargetSessionAttributes.PreferPrimary =>
                BlueTuskTargetSessionAttributes.Primary,
            BlueTuskTargetSessionAttributes.PreferStandby =>
                BlueTuskTargetSessionAttributes.Standby,
            _ => target,
        };
        var allowsFallback = target is
            BlueTuskTargetSessionAttributes.PreferPrimary or
            BlueTuskTargetSessionAttributes.PreferStandby;
        var failures = new List<Exception>();
        BlueTuskPhysicalSession? fallback = null;
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlueTuskPhysicalSession? candidate = null;
            try
            {
                candidate = await OpenEndpointAsync(
                    settings,
                    endpoint,
                    cancellationToken).ConfigureAwait(false);
                if (requiredTarget == BlueTuskTargetSessionAttributes.Any)
                {
                    if (fallback is not null)
                    {
                        await fallback.DisposeAsync().ConfigureAwait(false);
                    }

                    var accepted = candidate;
                    candidate = null;
                    return accepted;
                }

                await candidate.RefreshHostStateAsync(cancellationToken).ConfigureAwait(false);
                if (MatchesTarget(candidate, requiredTarget))
                {
                    if (fallback is not null)
                    {
                        await fallback.DisposeAsync().ConfigureAwait(false);
                    }

                    var accepted = candidate;
                    candidate = null;
                    return accepted;
                }

                failures.Add(new BlueTuskHostSelectionException(
                    endpoint,
                    requiredTarget,
                    candidate.IsPrimary,
                    candidate.IsReadOnly));
                if (allowsFallback && fallback is null)
                {
                    fallback = candidate;
                    candidate = null;
                }
            }
            catch (BlueTuskAuthenticationException)
            {
                if (fallback is not null)
                {
                    await fallback.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException)
            {
                if (fallback is not null)
                {
                    await fallback.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(new BlueTuskHostConnectionException(endpoint, exception));
            }
            finally
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        throw new BlueTuskException(
            $"Could not open a PostgreSQL connection matching {target} across " +
            $"{endpoints.Length} configured host(s).",
            new AggregateException(failures));
    }

    public async ValueTask RefreshHostStateAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _session.ExecuteSimpleQueryAsync(
            "SELECT pg_is_in_recovery(), current_setting('transaction_read_only')",
            cancellationToken).ConfigureAwait(false);
        var row = result.FirstOrDefault is { Rows.Count: 1, Fields.Count: 2 } resultSet
            ? resultSet.Rows[0]
            : throw new BlueTuskException(
                "PostgreSQL returned an invalid target-session probe result.");
        _isPrimary = !ReadPostgreSqlBoolean(row.Values[0], "pg_is_in_recovery");
        _isReadOnly = ReadPostgreSqlBoolean(row.Values[1], "transaction_read_only");
    }

    public async ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        if (!ResetsPreparedStatements(sql))
        {
            return await _session.ExecuteSimpleQueryAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        await _autoPrepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _session.ExecuteSimpleQueryAsync(sql, cancellationToken).ConfigureAwait(false);
            _autoPrepareEntries.Clear();
            return result;
        }
        finally
        {
            _autoPrepareGate.Release();
        }
    }

    public async ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default)
    {
        if (_maximumAutoPreparedStatements == 0)
        {
            return await _session.ExecuteExtendedQueryAsync(
                sql,
                parameters,
                useBinaryResults,
                cancellationToken).ConfigureAwait(false);
        }

        await _autoPrepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteAutoPreparedAsync(
                sql,
                parameters,
                useBinaryResults,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _autoPrepareGate.Release();
        }
    }

    public ValueTask PrepareStatementAsync(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids,
        CancellationToken cancellationToken = default) =>
        _session.PrepareStatementAsync(statementName, sql, parameterTypeOids, cancellationToken);

    public ValueTask<BlueTuskQueryResult> ExecutePreparedStatementAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default) =>
        _session.ExecutePreparedStatementAsync(
            statementName,
            parameters,
            useBinaryResults,
            cancellationToken);

    public ValueTask ClosePreparedStatementAsync(
        string statementName,
        CancellationToken cancellationToken = default) =>
        _session.ClosePreparedStatementAsync(statementName, cancellationToken);

    public ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
        IReadOnlyList<BlueTuskBatchQuery> queries,
        CancellationToken cancellationToken = default) =>
        _session.ExecuteBatchAsync(queries, cancellationToken);

    public ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
        IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
        CancellationToken cancellationToken = default) =>
        _session.ExecutePreparedBatchAsync(queries, cancellationToken);

    public ValueTask<BlueTuskCopyResult> CopyInAsync(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default) =>
        _session.CopyInAsync(sql, source, copyStarted, cancellationToken);

    public ValueTask<BlueTuskCopyResult> CopyOutAsync(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default) =>
        _session.CopyOutAsync(sql, destination, copyStarted, cancellationToken);

    public ValueTask<BlueTuskNotificationResponse> WaitForNotificationAsync(
        CancellationToken cancellationToken = default) =>
        _session.WaitForNotificationAsync(cancellationToken);

    public void Cancel() => _session.Cancel();

    public ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
        _session.CancelAsync(cancellationToken);

    public void Dispose()
    {
        _autoPrepareEntries.Clear();
        _autoPrepareGate.Dispose();
        _session.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _autoPrepareEntries.Clear();
        _autoPrepareGate.Dispose();
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<BlueTuskQueryResult> ExecuteAutoPreparedAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken)
    {
        var key = CreateAutoPrepareKey(sql, parameters);
        if (!_autoPrepareEntries.TryGetValue(key, out var entry))
        {
            entry = new AutoPrepareEntry();
            _autoPrepareEntries.Add(key, entry);
        }

        entry.LastUsed = ++_autoPrepareClock;
        PruneAutoPrepareCandidates(key);
        if (entry.PreparedStatementName is { } preparedStatementName)
        {
            try
            {
                return await _session.ExecutePreparedStatementAsync(
                    preparedStatementName,
                    parameters,
                    useBinaryResults,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception) when (exception.SqlState == "26000")
            {
                _autoPrepareEntries.Remove(key);
                return await _session.ExecuteExtendedQueryAsync(
                    sql,
                    parameters,
                    useBinaryResults,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        entry.UsageCount = entry.UsageCount == int.MaxValue
            ? int.MaxValue
            : entry.UsageCount + 1;
        if (entry.UsageCount < _autoPrepareMinimumUsages)
        {
            return await _session.ExecuteExtendedQueryAsync(
                sql,
                parameters,
                useBinaryResults,
                cancellationToken).ConfigureAwait(false);
        }

        await EvictAutoPreparedStatementIfRequiredAsync(cancellationToken).ConfigureAwait(false);
        preparedStatementName =
            $"bluetusk_auto_{++_autoPrepareNameSequence:x}";
        await _session.PrepareStatementAsync(
            preparedStatementName,
            sql,
            parameters.Select(static parameter => parameter.TypeOid).ToArray(),
            cancellationToken).ConfigureAwait(false);
        entry.PreparedStatementName = preparedStatementName;
        return await _session.ExecutePreparedStatementAsync(
            preparedStatementName,
            parameters,
            useBinaryResults,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EvictAutoPreparedStatementIfRequiredAsync(
        CancellationToken cancellationToken)
    {
        var prepared = _autoPrepareEntries
            .Where(static pair => pair.Value.PreparedStatementName is not null)
            .ToArray();
        if (prepared.Length < _maximumAutoPreparedStatements)
        {
            return;
        }

        var oldest = prepared.MinBy(static pair => pair.Value.LastUsed);
        try
        {
            await _session.ClosePreparedStatementAsync(
                oldest.Value.PreparedStatementName!,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BlueTuskServerException exception) when (exception.SqlState == "26000")
        {
            // Server-side DEALLOCATE invalidated the local entry; eviction can still continue.
        }

        _autoPrepareEntries.Remove(oldest.Key);
    }

    private void PruneAutoPrepareCandidates(string protectedKey)
    {
        var maximumCandidates = (int)Math.Max(
            32L,
            Math.Min(int.MaxValue, (long)_maximumAutoPreparedStatements * 4));
        if (_autoPrepareEntries.Count <= maximumCandidates)
        {
            return;
        }

        var oldest = _autoPrepareEntries
            .Where(pair =>
                pair.Value.PreparedStatementName is null &&
                !string.Equals(pair.Key, protectedKey, StringComparison.Ordinal))
            .MinBy(static pair => pair.Value.LastUsed);
        if (!string.IsNullOrEmpty(oldest.Key))
        {
            _autoPrepareEntries.Remove(oldest.Key);
        }
    }

    private static string CreateAutoPrepareKey(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters) =>
        string.Join(',', parameters.Select(static parameter => parameter.TypeOid)) +
        '\0' +
        sql;

    private static bool ResetsPreparedStatements(string sql)
    {
        var command = sql.Trim().TrimEnd(';').TrimEnd();
        return command.Equals("DISCARD ALL", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("DEALLOCATE ALL", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AutoPrepareEntry
    {
        public int UsageCount { get; set; }

        public long LastUsed { get; set; }

        public string? PreparedStatementName { get; set; }
    }

    private static async ValueTask<BlueTuskPhysicalSession> OpenEndpointAsync(
        BlueTuskConnectionStringBuilder settings,
        BlueTuskHostEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = endpoint.Host,
                Port = endpoint.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                ApplicationName = settings.ApplicationName,
                ConnectTimeout = settings.Timeout,
                SslMode = settings.SslMode,
                ChannelBinding = settings.ChannelBinding,
            },
            cancellationToken).ConfigureAwait(false);
        return new BlueTuskPhysicalSession(
            session,
            endpoint,
            settings.MaxAutoPrepare,
            settings.AutoPrepareMinUsages);
    }

    private static bool MatchesTarget(
        BlueTuskPhysicalSession session,
        BlueTuskTargetSessionAttributes target) => target switch
        {
            BlueTuskTargetSessionAttributes.Any => true,
            BlueTuskTargetSessionAttributes.Primary => session.IsPrimary == true,
            BlueTuskTargetSessionAttributes.Standby => session.IsPrimary == false,
            BlueTuskTargetSessionAttributes.ReadWrite => session.IsReadOnly == false,
            BlueTuskTargetSessionAttributes.ReadOnly => session.IsReadOnly == true,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private static bool ReadPostgreSqlBoolean(
        ReadOnlyMemory<byte>? value,
        string fieldName)
    {
        if (value is not { } bytes)
        {
            throw new BlueTuskException(
                $"PostgreSQL returned NULL for target-session field {fieldName}.");
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes.Span);
        return text switch
        {
            "t" or "true" or "on" => true,
            "f" or "false" or "off" => false,
            _ => throw new BlueTuskException(
                $"PostgreSQL returned invalid target-session field {fieldName}.")
        };
    }

    private sealed class BlueTuskHostSelectionException(
        BlueTuskHostEndpoint endpoint,
        BlueTuskTargetSessionAttributes target,
        bool? isPrimary,
        bool? isReadOnly)
        : Exception(
            $"Host {endpoint} does not match {target} " +
            $"(primary={isPrimary}, read-only={isReadOnly}).");

    private sealed class BlueTuskHostConnectionException(
        BlueTuskHostEndpoint endpoint,
        Exception innerException)
        : Exception($"Host {endpoint} could not be opened.", innerException);
}
