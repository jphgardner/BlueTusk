using BlueTusk.Client;
using BlueTusk.Diagnostics;
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

    BlueTuskServerCapabilities Capabilities => BlueTuskServerCapabilities.Unknown;

    BlueTuskTransactionStatus TransactionStatus { get; }

    void RefreshHostState() =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    ValueTask RefreshHostStateAsync(CancellationToken cancellationToken = default);

    BlueTuskQueryResult ExecuteSimpleQuery(string sql) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
        string sql,
        CancellationToken cancellationToken = default);

    BlueTuskQueryResult ExecuteExtendedQuery(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default);

    BlueTuskPortal BeginPortal(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize) =>
        throw new NotSupportedException("This physical-session implementation does not provide streaming portals.");

    ValueTask<BlueTuskPortal> BeginPortalAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This physical-session implementation does not provide streaming portals.");

    ValueTask PrepareStatementAsync(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids,
        CancellationToken cancellationToken = default);

    void PrepareStatement(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    BlueTuskQueryResult ExecutePreparedStatement(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    ValueTask<BlueTuskQueryResult> ExecutePreparedStatementAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default);

    BlueTuskPortal BeginPreparedPortal(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize) =>
        throw new NotSupportedException("This physical-session implementation does not provide streaming portals.");

    ValueTask<BlueTuskPortal> BeginPreparedPortalAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This physical-session implementation does not provide streaming portals.");

    ValueTask ClosePreparedStatementAsync(
        string statementName,
        CancellationToken cancellationToken = default);

    void ClosePreparedStatement(string statementName) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    BlueTuskQueryResult ExecuteBatch(IReadOnlyList<BlueTuskBatchQuery> queries) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
        IReadOnlyList<BlueTuskBatchQuery> queries,
        CancellationToken cancellationToken = default);

    ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
        IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
        CancellationToken cancellationToken = default);

    BlueTuskQueryResult ExecutePreparedBatch(IReadOnlyList<BlueTuskPreparedBatchQuery> queries) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    BlueTuskCopyResult CopyIn(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    BlueTuskCopyInOperation BeginCopyIn(string sql) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

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

    BlueTuskCopyResult CopyOut(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    BlueTuskCopyOutOperation BeginCopyOut(string sql) =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

    BlueTuskNotificationResponse WaitForNotification() =>
        throw new NotSupportedException("This physical-session implementation does not provide synchronous I/O.");

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

    public BlueTuskServerCapabilities Capabilities => _session.Capabilities;

    public BlueTuskTransactionStatus TransactionStatus => _session.TransactionStatus;

    public static IBlueTuskPhysicalSession Open(
        BlueTuskConnectionStringBuilder settings,
        BlueTuskClientConfiguration? clientConfiguration = null)
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
            BlueTuskPhysicalSession? candidate = null;
            try
            {
                candidate = OpenEndpoint(settings, endpoint, clientConfiguration);
                if (requiredTarget == BlueTuskTargetSessionAttributes.Any)
                {
                    fallback?.Dispose();
                    var accepted = candidate;
                    candidate = null;
                    return accepted;
                }

                candidate.RefreshHostState();
                if (MatchesTarget(candidate, requiredTarget))
                {
                    fallback?.Dispose();
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
                fallback?.Dispose();
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(new BlueTuskHostConnectionException(endpoint, exception));
            }
            finally
            {
                candidate?.Dispose();
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

    public static async ValueTask<IBlueTuskPhysicalSession> OpenAsync(
        BlueTuskConnectionStringBuilder settings,
        CancellationToken cancellationToken) =>
        await OpenAsync(settings, BlueTuskClientConfiguration.Empty, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<IBlueTuskPhysicalSession> OpenAsync(
        BlueTuskConnectionStringBuilder settings,
        BlueTuskClientConfiguration? clientConfiguration,
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
                    clientConfiguration,
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

    public void RefreshHostState()
    {
        var result = _session.ExecuteSimpleQuery(
            "SELECT pg_is_in_recovery(), current_setting('transaction_read_only')");
        var row = result.FirstOrDefault is { Rows.Count: 1, Fields.Count: 2 } resultSet
            ? resultSet.Rows[0]
            : throw new BlueTuskException(
                "PostgreSQL returned an invalid target-session probe result.");
        _isPrimary = !ReadPostgreSqlBoolean(row.Values[0], "pg_is_in_recovery");
        _isReadOnly = ReadPostgreSqlBoolean(row.Values[1], "transaction_read_only");
    }

    public BlueTuskQueryResult ExecuteSimpleQuery(string sql)
    {
        if (!ResetsPreparedStatements(sql))
        {
            return _session.ExecuteSimpleQuery(sql);
        }

        _autoPrepareGate.Wait();
        try
        {
            var result = _session.ExecuteSimpleQuery(sql);
            BlueTuskDiagnostics.RecordPreparedStatements(
                _autoPrepareEntries.Count(static pair => pair.Value.PreparedStatementName is not null),
                "automatic",
                "invalidate");
            _autoPrepareEntries.Clear();
            return result;
        }
        finally
        {
            _autoPrepareGate.Release();
        }
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
            BlueTuskDiagnostics.RecordPreparedStatements(
                _autoPrepareEntries.Count(static pair => pair.Value.PreparedStatementName is not null),
                "automatic",
                "invalidate");
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

    public BlueTuskPortal BeginPortal(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize) =>
        _session.BeginPortal(sql, parameters, useBinaryResults, fetchSize);

    public ValueTask<BlueTuskPortal> BeginPortalAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize,
        CancellationToken cancellationToken = default) =>
        _session.BeginPortalAsync(
            sql,
            parameters,
            useBinaryResults,
            fetchSize,
            cancellationToken);

    public BlueTuskQueryResult ExecuteExtendedQuery(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults)
    {
        if (_maximumAutoPreparedStatements == 0)
        {
            return _session.ExecuteExtendedQuery(sql, parameters, useBinaryResults);
        }

        _autoPrepareGate.Wait();
        try
        {
            return ExecuteAutoPrepared(sql, parameters, useBinaryResults);
        }
        finally
        {
            _autoPrepareGate.Release();
        }
    }

    public void PrepareStatement(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids) =>
        _session.PrepareStatement(statementName, sql, parameterTypeOids);

    public ValueTask PrepareStatementAsync(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids,
        CancellationToken cancellationToken = default) =>
        _session.PrepareStatementAsync(statementName, sql, parameterTypeOids, cancellationToken);

    public BlueTuskQueryResult ExecutePreparedStatement(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults) =>
        _session.ExecutePreparedStatement(statementName, parameters, useBinaryResults);

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

    public BlueTuskPortal BeginPreparedPortal(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize) =>
        _session.BeginPreparedPortal(statementName, parameters, useBinaryResults, fetchSize);

    public ValueTask<BlueTuskPortal> BeginPreparedPortalAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize,
        CancellationToken cancellationToken = default) =>
        _session.BeginPreparedPortalAsync(
            statementName,
            parameters,
            useBinaryResults,
            fetchSize,
            cancellationToken);

    public ValueTask ClosePreparedStatementAsync(
        string statementName,
        CancellationToken cancellationToken = default) =>
        _session.ClosePreparedStatementAsync(statementName, cancellationToken);

    public void ClosePreparedStatement(string statementName) =>
        _session.ClosePreparedStatement(statementName);

    public BlueTuskQueryResult ExecuteBatch(IReadOnlyList<BlueTuskBatchQuery> queries) =>
        _session.ExecuteBatch(queries);

    public ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
        IReadOnlyList<BlueTuskBatchQuery> queries,
        CancellationToken cancellationToken = default) =>
        _session.ExecuteBatchAsync(queries, cancellationToken);

    public ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
        IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
        CancellationToken cancellationToken = default) =>
        _session.ExecutePreparedBatchAsync(queries, cancellationToken);

    public BlueTuskQueryResult ExecutePreparedBatch(IReadOnlyList<BlueTuskPreparedBatchQuery> queries) =>
        _session.ExecutePreparedBatch(queries);

    public ValueTask<BlueTuskCopyResult> CopyInAsync(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default) =>
        _session.CopyInAsync(sql, source, copyStarted, cancellationToken);

    public BlueTuskCopyResult CopyIn(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted) =>
        _session.CopyIn(sql, source, copyStarted);

    public BlueTuskCopyInOperation BeginCopyIn(string sql) => _session.BeginCopyIn(sql);

    public ValueTask<BlueTuskCopyResult> CopyOutAsync(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default) =>
        _session.CopyOutAsync(sql, destination, copyStarted, cancellationToken);

    public BlueTuskCopyResult CopyOut(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted) =>
        _session.CopyOut(sql, destination, copyStarted);

    public BlueTuskCopyOutOperation BeginCopyOut(string sql) => _session.BeginCopyOut(sql);

    public BlueTuskNotificationResponse WaitForNotification() =>
        _session.WaitForNotification();

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
                BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "reuse");
                return await _session.ExecutePreparedStatementAsync(
                    preparedStatementName,
                    parameters,
                    useBinaryResults,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception) when (exception.SqlState == "26000")
            {
                BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "invalidate");
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
        BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "prepare");
        entry.PreparedStatementName = preparedStatementName;
        return await _session.ExecutePreparedStatementAsync(
            preparedStatementName,
            parameters,
            useBinaryResults,
            cancellationToken).ConfigureAwait(false);
    }

    private BlueTuskQueryResult ExecuteAutoPrepared(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults)
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
                BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "reuse");
                return _session.ExecutePreparedStatement(
                    preparedStatementName,
                    parameters,
                    useBinaryResults);
            }
            catch (BlueTuskServerException exception) when (exception.SqlState == "26000")
            {
                BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "invalidate");
                _autoPrepareEntries.Remove(key);
                return _session.ExecuteExtendedQuery(sql, parameters, useBinaryResults);
            }
        }

        entry.UsageCount = entry.UsageCount == int.MaxValue
            ? int.MaxValue
            : entry.UsageCount + 1;
        if (entry.UsageCount < _autoPrepareMinimumUsages)
        {
            return _session.ExecuteExtendedQuery(sql, parameters, useBinaryResults);
        }

        EvictAutoPreparedStatementIfRequired();
        preparedStatementName = $"bluetusk_auto_{++_autoPrepareNameSequence:x}";
        _session.PrepareStatement(
            preparedStatementName,
            sql,
            parameters.Select(static parameter => parameter.TypeOid).ToArray());
        BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "prepare");
        entry.PreparedStatementName = preparedStatementName;
        return _session.ExecutePreparedStatement(
            preparedStatementName,
            parameters,
            useBinaryResults);
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
        BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "evict");
    }

    private void EvictAutoPreparedStatementIfRequired()
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
            _session.ClosePreparedStatement(oldest.Value.PreparedStatementName!);
        }
        catch (BlueTuskServerException exception) when (exception.SqlState == "26000")
        {
            // Server-side DEALLOCATE invalidated the local entry; eviction can still continue.
        }

        _autoPrepareEntries.Remove(oldest.Key);
        BlueTuskDiagnostics.RecordPreparedStatements(1, "automatic", "evict");
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
        BlueTuskClientConfiguration? clientConfiguration,
        CancellationToken cancellationToken)
    {
        var options = new BlueTuskClientOptions
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
            Passfile = settings.Passfile,
            KerberosServiceName = settings.KerberosServiceName,
            ApplicationName = settings.ApplicationName,
            ConnectTimeout = settings.Timeout,
            SslMode = settings.SslMode,
            ChannelBinding = settings.ChannelBinding,
            AllowUnencryptedPassword = settings.AllowUnencryptedPassword,
        };
        var session = await BlueTuskSession.OpenAsync(
            (clientConfiguration ?? BlueTuskClientConfiguration.Empty).Apply(options),
            cancellationToken).ConfigureAwait(false);
        await session.ProbeOptionalCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return new BlueTuskPhysicalSession(
            session,
            endpoint,
            settings.MaxAutoPrepare,
            settings.AutoPrepareMinUsages);
    }

    private static BlueTuskPhysicalSession OpenEndpoint(
        BlueTuskConnectionStringBuilder settings,
        BlueTuskHostEndpoint endpoint,
        BlueTuskClientConfiguration? clientConfiguration)
    {
        var options = new BlueTuskClientOptions
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
            Passfile = settings.Passfile,
            KerberosServiceName = settings.KerberosServiceName,
            ApplicationName = settings.ApplicationName,
            ConnectTimeout = settings.Timeout,
            SslMode = settings.SslMode,
            ChannelBinding = settings.ChannelBinding,
            AllowUnencryptedPassword = settings.AllowUnencryptedPassword,
        };
        var session = BlueTuskSession.Open(
            (clientConfiguration ?? BlueTuskClientConfiguration.Empty).Apply(options));
        session.ProbeOptionalCapabilities();
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
