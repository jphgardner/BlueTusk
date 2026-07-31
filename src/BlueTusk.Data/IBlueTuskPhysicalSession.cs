using BlueTusk.Client;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

internal interface IBlueTuskPhysicalSession : IDisposable, IAsyncDisposable
{
    bool IsOpen { get; }

    IReadOnlyDictionary<string, string> Parameters { get; }

    BlueTuskTransactionStatus TransactionStatus { get; }

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
    private readonly int _maximumAutoPreparedStatements;
    private readonly int _autoPrepareMinimumUsages;
    private readonly Dictionary<string, AutoPrepareEntry> _autoPrepareEntries =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _autoPrepareGate = new(1, 1);
    private long _autoPrepareClock;
    private long _autoPrepareNameSequence;

    private BlueTuskPhysicalSession(
        BlueTuskSession session,
        int maximumAutoPreparedStatements,
        int autoPrepareMinimumUsages)
    {
        _session = session;
        _maximumAutoPreparedStatements = maximumAutoPreparedStatements;
        _autoPrepareMinimumUsages = autoPrepareMinimumUsages;
    }

    public bool IsOpen => _session.IsOpen;

    public IReadOnlyDictionary<string, string> Parameters => _session.Parameters;

    public BlueTuskTransactionStatus TransactionStatus => _session.TransactionStatus;

    public static async ValueTask<IBlueTuskPhysicalSession> OpenAsync(
        BlueTuskConnectionStringBuilder settings,
        CancellationToken cancellationToken)
    {
        var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
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
            settings.MaxAutoPrepare,
            settings.AutoPrepareMinUsages);
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
}
