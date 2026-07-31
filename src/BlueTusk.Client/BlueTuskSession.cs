using System.Buffers;
using System.Diagnostics;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;
using BlueTusk.Security;
using BlueTusk.Transport;

namespace BlueTusk.Client;

/// <summary>A single authenticated PostgreSQL protocol session.</summary>
public sealed class BlueTuskSession : IAsyncDisposable, IDisposable
{
    private const int CopyBufferSize = 81_920;
    private static readonly short[] BinaryResultFormat = [1];
    private static readonly short[] TextResultFormat = [0];
    private readonly BlueTuskProtocolConnection _connection;
    private readonly BlueTuskClientOptions _options;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly List<BlueTuskError> _notices = [];
    private readonly Queue<BlueTuskNotificationResponse> _pendingNotifications = [];
    private readonly object _cancellationSync = new();
    private TaskCompletionSource<bool>? _cancellationRequest;
    private int _copyBothOperationActive;
    private int _synchronousCopyOperationActive;
    private int _portalOperationActive;
    private long _portalSequence;
    private bool _open;
    private bool _disposed;

    private BlueTuskSession(BlueTuskProtocolConnection connection, BlueTuskClientOptions options)
    {
        _connection = connection;
        _options = options;
    }

    public bool IsOpen => _open && !_disposed;

    public bool IsEncrypted => _connection.Transport is IBlueTuskTlsTransport { IsEncrypted: true };

    public IReadOnlyDictionary<string, string> Parameters => _parameters;

    public IReadOnlyList<BlueTuskError> Notices => _notices;

    public BlueTuskBackendKeyData? BackendKeyData { get; private set; }

    public BlueTuskTransactionStatus TransactionStatus { get; private set; } = BlueTuskTransactionStatus.Idle;

    /// <summary>Gets the capabilities detected from the authenticated PostgreSQL server.</summary>
    public BlueTuskServerCapabilities Capabilities { get; private set; } = BlueTuskServerCapabilities.Unknown;

    public static BlueTuskSession Open(BlueTuskClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var connection = new BlueTuskProtocolConnection(new BlueTuskSocketTransport());
        var session = new BlueTuskSession(connection, options);
        try
        {
            session.OpenCore();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public static async ValueTask<BlueTuskSession> OpenAsync(
        BlueTuskClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var connection = new BlueTuskProtocolConnection(new BlueTuskSocketTransport());
        var session = new BlueTuskSession(connection, options);
        try
        {
            await session.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(sql);
        return ExecuteQueryAsync(
            output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql),
            cancellationToken);
    }

    public BlueTuskQueryResult ExecuteSimpleQuery(string sql)
    {
        ValidateQuery(sql);
        return ExecuteQuery(output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql));
    }

    public BlueTuskQueryResult ExecuteExtendedQuery(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters) =>
        ExecuteExtendedQuery(sql, parameters, useBinaryResults: true);

    public BlueTuskQueryResult ExecuteExtendedQuery(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var typeOids = new uint[parameters.Count];
        var bindParameters = new BlueTuskBindParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            typeOids[index] = parameter.TypeOid;
            bindParameters[index] = new BlueTuskBindParameter(parameter.FormatCode, parameter.Value);
        }

        return ExecuteQuery(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteParse(output, string.Empty, sql, typeOids);
                BlueTuskFrontendMessageWriter.WriteBind(
                    output,
                    string.Empty,
                    string.Empty,
                    bindParameters,
                    useBinaryResults ? BinaryResultFormat : TextResultFormat);
                BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            });
    }

    public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        CancellationToken cancellationToken = default) =>
        ExecuteExtendedQueryAsync(sql, parameters, useBinaryResults: true, cancellationToken);

    public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var typeOids = new uint[parameters.Count];
        var bindParameters = new BlueTuskBindParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            typeOids[index] = parameter.TypeOid;
            bindParameters[index] = new BlueTuskBindParameter(parameter.FormatCode, parameter.Value);
        }

        return ExecuteQueryAsync(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteParse(output, string.Empty, sql, typeOids);
                BlueTuskFrontendMessageWriter.WriteBind(
                    output,
                    string.Empty,
                    string.Empty,
                    bindParameters,
                    useBinaryResults ? BinaryResultFormat : TextResultFormat);
                BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken);
    }

    /// <summary>Begins an incremental extended-query operation over a bounded named portal.</summary>
    public BlueTuskPortal BeginPortal(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults = true,
        int fetchSize = 32)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfLessThan(fetchSize, 1);
        var portalName = $"bluetusk_portal_{Interlocked.Increment(ref _portalSequence):x}";
        return BeginPortalCore(portalName, string.Empty, sql, parameters, useBinaryResults, fetchSize);
    }

    /// <summary>Begins an incremental extended-query operation over a bounded named portal.</summary>
    public async ValueTask<BlueTuskPortal> BeginPortalAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults = true,
        int fetchSize = 32,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfLessThan(fetchSize, 1);
        var portalName = $"bluetusk_portal_{Interlocked.Increment(ref _portalSequence):x}";
        return await BeginPortalCoreAsync(
            portalName,
            string.Empty,
            sql,
            parameters,
            useBinaryResults,
            fetchSize,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Begins an incremental execution of a named prepared statement.</summary>
    public BlueTuskPortal BeginPreparedPortal(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults = true,
        int fetchSize = 32)
    {
        ValidatePreparedStatementName(statementName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfLessThan(fetchSize, 1);
        var portalName = $"bluetusk_portal_{Interlocked.Increment(ref _portalSequence):x}";
        return BeginPortalCore(portalName, statementName, null, parameters, useBinaryResults, fetchSize);
    }

    /// <summary>Begins an incremental execution of a named prepared statement.</summary>
    public async ValueTask<BlueTuskPortal> BeginPreparedPortalAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults = true,
        int fetchSize = 32,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedStatementName(statementName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfLessThan(fetchSize, 1);
        var portalName = $"bluetusk_portal_{Interlocked.Increment(ref _portalSequence):x}";
        return await BeginPortalCoreAsync(
            portalName,
            statementName,
            null,
            parameters,
            useBinaryResults,
            fetchSize,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a named PostgreSQL prepared statement.</summary>
    public async ValueTask PrepareStatementAsync(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedStatementName(statementName);
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(parameterTypeOids);

        _ = await ExecuteQueryAsync(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteParse(
                    output,
                    statementName,
                    sql,
                    parameterTypeOids);
                BlueTuskFrontendMessageWriter.WriteDescribeStatement(output, statementName);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a named PostgreSQL prepared statement.</summary>
    public void PrepareStatement(
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids)
    {
        ValidatePreparedStatementName(statementName);
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(parameterTypeOids);

        _ = ExecuteQuery(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteParse(
                    output,
                    statementName,
                    sql,
                    parameterTypeOids);
                BlueTuskFrontendMessageWriter.WriteDescribeStatement(output, statementName);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            });
    }

    /// <summary>Executes a named PostgreSQL prepared statement.</summary>
    public ValueTask<BlueTuskQueryResult> ExecutePreparedStatementAsync(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults = true,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedStatementName(statementName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        ArgumentNullException.ThrowIfNull(parameters);
        var bindParameters = new BlueTuskBindParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            bindParameters[index] = new BlueTuskBindParameter(parameter.FormatCode, parameter.Value);
        }

        return ExecuteQueryAsync(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteBind(
                    output,
                    string.Empty,
                    statementName,
                    bindParameters,
                    useBinaryResults ? BinaryResultFormat : TextResultFormat);
                BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken);
    }

    /// <summary>Executes a named PostgreSQL prepared statement.</summary>
    public BlueTuskQueryResult ExecutePreparedStatement(
        string statementName,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults = true)
    {
        ValidatePreparedStatementName(statementName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        ArgumentNullException.ThrowIfNull(parameters);
        var bindParameters = new BlueTuskBindParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            bindParameters[index] = new BlueTuskBindParameter(parameter.FormatCode, parameter.Value);
        }

        return ExecuteQuery(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteBind(
                    output,
                    string.Empty,
                    statementName,
                    bindParameters,
                    useBinaryResults ? BinaryResultFormat : TextResultFormat);
                BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            });
    }

    /// <summary>Closes a named PostgreSQL prepared statement.</summary>
    public async ValueTask ClosePreparedStatementAsync(
        string statementName,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedStatementName(statementName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        _ = await ExecuteQueryAsync(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteCloseStatement(output, statementName);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes a named PostgreSQL prepared statement.</summary>
    public void ClosePreparedStatement(string statementName)
    {
        ValidatePreparedStatementName(statementName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        _ = ExecuteQuery(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteCloseStatement(output, statementName);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            });
    }

    /// <summary>Executes multiple unnamed extended-query statements in one protocol cycle.</summary>
    public ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
        IReadOnlyList<BlueTuskBatchQuery> queries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
        {
            throw new ArgumentException("A batch requires at least one query.", nameof(queries));
        }

        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidateQuery(query.Sql);
            ArgumentNullException.ThrowIfNull(query.Parameters);
        }

        return ExecuteQueryAsync(
            output =>
            {
                foreach (var query in queries)
                {
                    var typeOids = query.Parameters
                        .Select(static parameter => parameter.TypeOid)
                        .ToArray();
                    var bindParameters = query.Parameters
                        .Select(static parameter =>
                            new BlueTuskBindParameter(parameter.FormatCode, parameter.Value))
                        .ToArray();
                    BlueTuskFrontendMessageWriter.WriteParse(
                        output,
                        string.Empty,
                        query.Sql,
                        typeOids);
                    BlueTuskFrontendMessageWriter.WriteBind(
                        output,
                        string.Empty,
                        string.Empty,
                        bindParameters,
                        query.UseBinaryResults ? BinaryResultFormat : TextResultFormat);
                    BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                    BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                }

                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken);
    }

    /// <summary>Executes multiple unnamed extended-query statements in one protocol cycle.</summary>
    public BlueTuskQueryResult ExecuteBatch(IReadOnlyList<BlueTuskBatchQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
        {
            throw new ArgumentException("A batch requires at least one query.", nameof(queries));
        }

        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidateQuery(query.Sql);
            ArgumentNullException.ThrowIfNull(query.Parameters);
        }

        return ExecuteQuery(
            output =>
            {
                foreach (var query in queries)
                {
                    var typeOids = query.Parameters
                        .Select(static parameter => parameter.TypeOid)
                        .ToArray();
                    var bindParameters = query.Parameters
                        .Select(static parameter =>
                            new BlueTuskBindParameter(parameter.FormatCode, parameter.Value))
                        .ToArray();
                    BlueTuskFrontendMessageWriter.WriteParse(
                        output,
                        string.Empty,
                        query.Sql,
                        typeOids);
                    BlueTuskFrontendMessageWriter.WriteBind(
                        output,
                        string.Empty,
                        string.Empty,
                        bindParameters,
                        query.UseBinaryResults ? BinaryResultFormat : TextResultFormat);
                    BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                    BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                }

                BlueTuskFrontendMessageWriter.WriteSync(output);
            });
    }

    /// <summary>
    /// Executes ordered extended-query groups in PostgreSQL pipeline mode. Each group ends with an explicit
    /// Sync boundary, and server errors are returned with that group after the session has reached ReadyForQuery.
    /// </summary>
    public BlueTuskPipelineResult ExecutePipeline(IReadOnlyList<BlueTuskPipelineGroup> groups)
    {
        ValidatePipeline(groups);
        _operationLock.Wait();
        var started = Stopwatch.GetTimestamp();
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            _connection.Write(output => WritePipeline(output, groups));

            var results = new List<BlueTuskPipelineGroupResult>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                results.Add(ReadPipelineGroupResponse());
                if (index + 1 < groups.Count)
                {
                    BeginNextPipelineGroup();
                }
            }

            return new BlueTuskPipelineResult(results);
        }
        catch
        {
            if (_connection.StateMachine.State is not (
                    BlueTuskConnectionState.Ready or BlueTuskConnectionState.FailedTransaction))
            {
                _open = false;
            }

            throw;
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Executes ordered extended-query groups in PostgreSQL pipeline mode. Cancellation drains the active and
    /// already-sent groups before the session is released for reuse.
    /// </summary>
    public async ValueTask<BlueTuskPipelineResult> ExecutePipelineAsync(
        IReadOnlyList<BlueTuskPipelineGroup> groups,
        CancellationToken cancellationToken = default)
    {
        ValidatePipeline(groups);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var messagesWritten = false;
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            await _connection.WriteAsync(
                output => WritePipeline(output, groups),
                cancellationToken).ConfigureAwait(false);
            messagesWritten = true;

            var results = new List<BlueTuskPipelineGroupResult>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                try
                {
                    results.Add(await ReadPipelineGroupResponseWithCancellationAsync(cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await DrainRemainingPipelineGroupsAsync(groups.Count - index - 1).ConfigureAwait(false);
                    throw;
                }

                if (index + 1 < groups.Count)
                {
                    BeginNextPipelineGroup();
                }
            }

            return new BlueTuskPipelineResult(results);
        }
        catch
        {
            if (!messagesWritten || _connection.StateMachine.State is not (
                    BlueTuskConnectionState.Ready or BlueTuskConnectionState.FailedTransaction))
            {
                _open = false;
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    /// <summary>Executes multiple named prepared statements in one protocol cycle.</summary>
    public ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
        IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
        {
            throw new ArgumentException("A batch requires at least one query.", nameof(queries));
        }

        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidatePreparedStatementName(query.StatementName);
            ArgumentNullException.ThrowIfNull(query.Parameters);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        return ExecuteQueryAsync(
            output =>
            {
                foreach (var query in queries)
                {
                    var bindParameters = query.Parameters
                        .Select(static parameter =>
                            new BlueTuskBindParameter(parameter.FormatCode, parameter.Value))
                        .ToArray();
                    BlueTuskFrontendMessageWriter.WriteBind(
                        output,
                        string.Empty,
                        query.StatementName,
                        bindParameters,
                        query.UseBinaryResults ? BinaryResultFormat : TextResultFormat);
                    BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                    BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                }

                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken);
    }

    /// <summary>Executes multiple named prepared statements in one protocol cycle.</summary>
    public BlueTuskQueryResult ExecutePreparedBatch(
        IReadOnlyList<BlueTuskPreparedBatchQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
        {
            throw new ArgumentException("A batch requires at least one query.", nameof(queries));
        }

        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidatePreparedStatementName(query.StatementName);
            ArgumentNullException.ThrowIfNull(query.Parameters);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        return ExecuteQuery(
            output =>
            {
                foreach (var query in queries)
                {
                    var bindParameters = query.Parameters
                        .Select(static parameter =>
                            new BlueTuskBindParameter(parameter.FormatCode, parameter.Value))
                        .ToArray();
                    BlueTuskFrontendMessageWriter.WriteBind(
                        output,
                        string.Empty,
                        query.StatementName,
                        bindParameters,
                        query.UseBinaryResults ? BinaryResultFormat : TextResultFormat);
                    BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                    BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                }

                BlueTuskFrontendMessageWriter.WriteSync(output);
            });
    }

    public BlueTuskCopyInOperation BeginCopyIn(string sql)
    {
        ValidateQuery(sql);
        _operationLock.Wait();
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            _connection.Write(output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql));
            var response = ReadCopyStart('G');
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyIn);
            Volatile.Write(ref _synchronousCopyOperationActive, 1);
            return new BlueTuskCopyInOperation(this, response);
        }
        catch
        {
            _operationLock.Release();
            throw;
        }
    }

    public BlueTuskCopyOutOperation BeginCopyOut(string sql)
    {
        ValidateQuery(sql);
        _operationLock.Wait();
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            _connection.Write(output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql));
            var response = ReadCopyStart('H');
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyOut);
            Volatile.Write(ref _synchronousCopyOperationActive, 1);
            return new BlueTuskCopyOutOperation(this, response);
        }
        catch
        {
            _operationLock.Release();
            throw;
        }
    }

    public BlueTuskCopyResult CopyIn(string sql, Stream source) =>
        CopyIn(sql, source, copyStarted: null);

    public BlueTuskCopyResult CopyIn(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The COPY source stream must be readable.", nameof(source));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        _operationLock.Wait();
        var started = Stopwatch.GetTimestamp();
        try
        {
            return CopyInCore(sql, source, copyStarted);
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    public BlueTuskCopyResult CopyOut(string sql, Stream destination) =>
        CopyOut(sql, destination, copyStarted: null);

    public BlueTuskCopyResult CopyOut(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The COPY destination stream must be writable.",
                nameof(destination));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        _operationLock.Wait();
        var started = Stopwatch.GetTimestamp();
        try
        {
            return CopyOutCore(sql, destination, copyStarted);
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    public async ValueTask<BlueTuskCopyResult> CopyInAsync(
        string sql,
        Stream source,
        CancellationToken cancellationToken = default) =>
        await CopyInAsync(
            sql,
            source,
            copyStarted: null,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<BlueTuskCopyResult> CopyInAsync(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The COPY source stream must be readable.", nameof(source));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await CopyInCoreAsync(
                sql,
                source,
                copyStarted,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    public async ValueTask<BlueTuskCopyResult> CopyOutAsync(
        string sql,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        await CopyOutAsync(
            sql,
            destination,
            copyStarted: null,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<BlueTuskCopyResult> CopyOutAsync(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(sql);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The COPY destination stream must be writable.",
                nameof(destination));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await CopyOutCoreAsync(
                sql,
                destination,
                copyStarted,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    /// <summary>Starts a duplex PostgreSQL COPY operation.</summary>
    public async ValueTask<BlueTuskCopyBothChannel> BeginCopyBothAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(sql);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            await _connection.WriteAsync(
                output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql),
                cancellationToken).ConfigureAwait(false);
            var response = await ReadCopyStartAsync('W').ConfigureAwait(false);
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyBoth);
            Volatile.Write(ref _copyBothOperationActive, 1);
            return new BlueTuskCopyBothChannel(this, response);
        }
        catch
        {
            _operationLock.Release();
            throw;
        }
    }

    /// <summary>Waits for the next asynchronous notification delivered by PostgreSQL.</summary>
    public BlueTuskNotificationResponse WaitForNotification()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        _operationLock.Wait();
        try
        {
            if (_pendingNotifications.TryDequeue(out var pending))
            {
                return pending;
            }

            if (_connection.StateMachine.State != BlueTuskConnectionState.Ready)
            {
                throw new InvalidOperationException(
                    "Notifications can only be awaited while the PostgreSQL session is ready.");
            }

            while (true)
            {
                var message = ReadMessage();
                switch (message.Identifier)
                {
                    case 'A':
                        return BlueTuskBackendMessageDecoder.DecodeNotificationResponse(message);
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'E':
                        throw new BlueTuskServerException(
                            BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    case 'Z':
                        CompleteReadyForQuerySynchronously(
                            BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Waits for the next asynchronous notification delivered by PostgreSQL.</summary>
    public async ValueTask<BlueTuskNotificationResponse> WaitForNotificationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingNotifications.TryDequeue(out var pending))
            {
                return pending;
            }

            if (_connection.StateMachine.State != BlueTuskConnectionState.Ready)
            {
                throw new InvalidOperationException(
                    "Notifications can only be awaited while the PostgreSQL session is ready.");
            }

            while (true)
            {
                var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Identifier)
                {
                    case 'A':
                        return BlueTuskBackendMessageDecoder.DecodeNotificationResponse(message);
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'E':
                        throw new BlueTuskServerException(
                            BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    case 'Z':
                        await CompleteReadyForQuery(
                            BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message)).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Cancel()
    {
        var completion = BeginCancellation();
        if (completion is null)
        {
            return;
        }

        try
        {
            BlueTuskCancellationChannel.Send(
                new BlueTuskEndpoint.Tcp(_options.Host, _options.Port),
                new BlueTuskTransportOptions { ConnectTimeout = _options.ConnectTimeout },
                GetBackendKeyData());
        }
        finally
        {
            CompleteCancellation(completion);
        }
    }

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        var completion = BeginCancellation();
        if (completion is null)
        {
            return;
        }

        try
        {
            await BlueTuskCancellationChannel.SendAsync(
                new BlueTuskEndpoint.Tcp(_options.Host, _options.Port),
                new BlueTuskTransportOptions { ConnectTimeout = _options.ConnectTimeout },
                GetBackendKeyData(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteCancellation(completion);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_open)
        {
            try
            {
                _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Closing);
                await _connection.WriteAsync(BlueTuskFrontendMessageWriter.WriteTerminate, CancellationToken.None)
                    .ConfigureAwait(false);
                _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Disconnected);
            }
            catch (IOException)
            {
                // The physical connection is being discarded regardless.
            }
        }

        _open = false;
        ReleaseCopyBothOperation();
        ReleaseSynchronousCopyOperation();
        Interlocked.Exchange(ref _portalOperationActive, 0);
        _operationLock.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _open = false;
        ReleaseCopyBothOperation();
        ReleaseSynchronousCopyOperation();
        Interlocked.Exchange(ref _portalOperationActive, 0);
        _operationLock.Dispose();
        _connection.Dispose();
    }

    private BlueTuskPortal BeginPortalCore(
        string portalName,
        string statementName,
        string? sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize)
    {
        var typeOids = new uint[parameters.Count];
        var bindParameters = new BlueTuskBindParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            typeOids[index] = parameters[index].TypeOid;
            bindParameters[index] = new BlueTuskBindParameter(
                parameters[index].FormatCode,
                parameters[index].Value);
        }

        _operationLock.Wait();
        Volatile.Write(ref _portalOperationActive, 1);
        var started = Stopwatch.GetTimestamp();
        var requestWritten = false;
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            _connection.Write(
                output => WritePortalStart(
                    output,
                    portalName,
                    statementName,
                    sql,
                    typeOids,
                    bindParameters,
                    useBinaryResults));
            requestWritten = true;
            var fields = ReadPortalStart();
            _connection.Write(
                output => WritePortalExecute(output, portalName, fetchSize));
            return new BlueTuskPortal(this, portalName, fields, fetchSize, started);
        }
        catch
        {
            if (requestWritten)
            {
                RecoverAbandonedPortal(portalName);
            }
            else
            {
                _open = false;
            }

            ReleasePortalOperation(started);
            throw;
        }
    }

    private async ValueTask<BlueTuskPortal> BeginPortalCoreAsync(
        string portalName,
        string statementName,
        string? sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        var typeOids = new uint[parameters.Count];
        var bindParameters = new BlueTuskBindParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            typeOids[index] = parameters[index].TypeOid;
            bindParameters[index] = new BlueTuskBindParameter(
                parameters[index].FormatCode,
                parameters[index].Value);
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _portalOperationActive, 1);
        var started = Stopwatch.GetTimestamp();
        var requestWritten = false;
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            await _connection.WriteAsync(
                output => WritePortalStart(
                    output,
                    portalName,
                    statementName,
                    sql,
                    typeOids,
                    bindParameters,
                    useBinaryResults),
                cancellationToken).ConfigureAwait(false);
            requestWritten = true;
            var fields = await ReadPortalStartAsync(cancellationToken).ConfigureAwait(false);
            await _connection.WriteAsync(
                output => WritePortalExecute(output, portalName, fetchSize),
                cancellationToken).ConfigureAwait(false);
            return new BlueTuskPortal(this, portalName, fields, fetchSize, started);
        }
        catch
        {
            if (requestWritten)
            {
                await RecoverAbandonedPortalAsync(portalName).ConfigureAwait(false);
            }
            else
            {
                _open = false;
            }

            ReleasePortalOperation(started);
            throw;
        }
    }

    private static void WritePortalStart(
        IBufferWriter<byte> output,
        string portalName,
        string statementName,
        string? sql,
        IReadOnlyList<uint> typeOids,
        IReadOnlyList<BlueTuskBindParameter> parameters,
        bool useBinaryResults)
    {
        if (sql is not null)
        {
            BlueTuskFrontendMessageWriter.WriteParse(output, statementName, sql, typeOids);
        }

        BlueTuskFrontendMessageWriter.WriteBind(
            output,
            portalName,
            statementName,
            parameters,
            useBinaryResults ? BinaryResultFormat : TextResultFormat);
        BlueTuskFrontendMessageWriter.WriteDescribePortal(output, portalName);
        BlueTuskFrontendMessageWriter.WriteFlush(output);
    }

    private static void WritePortalExecute(
        IBufferWriter<byte> output,
        string portalName,
        int fetchSize)
    {
        BlueTuskFrontendMessageWriter.WriteExecute(output, portalName, fetchSize);
        BlueTuskFrontendMessageWriter.WriteFlush(output);
    }

    private IReadOnlyList<BlueTuskFieldDescription> ReadPortalStart()
    {
        while (true)
        {
            var message = ReadStreamedMessage();
            switch (message.Identifier)
            {
                case '1':
                case '2':
                    break;
                case 'T':
                    return BlueTuskBackendMessageDecoder.DecodeRowDescription(message);
                case 'n':
                    return [];
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'E':
                    throw new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                default:
                    break;
            }
        }
    }

    private async ValueTask<IReadOnlyList<BlueTuskFieldDescription>> ReadPortalStartAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadStreamedMessageAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Identifier)
            {
                case '1':
                case '2':
                    break;
                case 'T':
                    return BlueTuskBackendMessageDecoder.DecodeRowDescription(message);
                case 'n':
                    return [];
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'E':
                    throw new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                default:
                    break;
            }
        }
    }

    internal BlueTuskPortalRow? ReadPortalRow(BlueTuskPortal portal)
    {
        EnsurePortalOperation();
        while (true)
        {
            var header = ReadStreamedMessageHeader();
            if (header.Identifier == 'D')
            {
                return new BlueTuskPortalRow(this, portal, header.PayloadLength, portal.Fields.Count);
            }

            var message = ReadStreamedMessage(header);
            switch (message.Identifier)
            {
                case 's':
                    RequestMorePortalRows(portal);
                    break;
                case 'C':
                    portal.SetCommandTag(BlueTuskBackendMessageDecoder.DecodeCommandComplete(message));
                    CompletePortal(portal);
                    return null;
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'E':
                    var error = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    RecoverPortalError();
                    portal.SetCompleted();
                    ReleasePortalOperation(portal.StartedTimestamp);
                    throw error;
                default:
                    break;
            }
        }
    }

    internal async ValueTask<BlueTuskPortalRow?> ReadPortalRowAsync(
        BlueTuskPortal portal,
        CancellationToken cancellationToken)
    {
        EnsurePortalOperation();
        while (true)
        {
            var header = await ReadStreamedMessageHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (header.Identifier == 'D')
            {
                return await BlueTuskPortalRow.CreateAsync(
                    this,
                    portal,
                    header.PayloadLength,
                    portal.Fields.Count,
                    cancellationToken).ConfigureAwait(false);
            }

            var message = await ReadStreamedMessageAsync(header, cancellationToken).ConfigureAwait(false);
            switch (message.Identifier)
            {
                case 's':
                    await RequestMorePortalRowsAsync(portal, cancellationToken).ConfigureAwait(false);
                    break;
                case 'C':
                    portal.SetCommandTag(BlueTuskBackendMessageDecoder.DecodeCommandComplete(message));
                    await CompletePortalAsync(portal).ConfigureAwait(false);
                    return null;
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'E':
                    var error = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    await RecoverPortalErrorAsync().ConfigureAwait(false);
                    portal.SetCompleted();
                    ReleasePortalOperation(portal.StartedTimestamp);
                    throw error;
                default:
                    break;
            }
        }
    }

    internal void ReadPortalPayloadExactly(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = _connection.ReadMessagePayload(destination);
            if (read == 0)
            {
                throw new BlueTuskProtocolException("A backend message payload ended unexpectedly.");
            }

            destination = destination[read..];
        }
    }

    internal async ValueTask ReadPortalPayloadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            var read = await _connection.ReadMessagePayloadAsync(
                destination,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new BlueTuskProtocolException("A backend message payload ended unexpectedly.");
            }

            destination = destination[read..];
        }
    }

    internal void AbortPortal(BlueTuskPortal portal)
    {
        if (Volatile.Read(ref _portalOperationActive) == 0)
        {
            return;
        }

        try
        {
            SkipActiveStreamedPayload();
            _connection.Write(
                output =>
                {
                    BlueTuskFrontendMessageWriter.WriteClosePortal(output, portal.Name);
                    BlueTuskFrontendMessageWriter.WriteSync(output);
                });
            DrainPortalToReady();
        }
        catch
        {
            _open = false;
        }
        finally
        {
            portal.SetCompleted();
            ReleasePortalOperation(portal.StartedTimestamp);
        }
    }

    internal async ValueTask AbortPortalAsync(BlueTuskPortal portal)
    {
        if (Volatile.Read(ref _portalOperationActive) == 0)
        {
            return;
        }

        try
        {
            await SkipActiveStreamedPayloadAsync().ConfigureAwait(false);
            await _connection.WriteAsync(
                output =>
                {
                    BlueTuskFrontendMessageWriter.WriteClosePortal(output, portal.Name);
                    BlueTuskFrontendMessageWriter.WriteSync(output);
                },
                CancellationToken.None).ConfigureAwait(false);
            await DrainPortalToReadyAsync().ConfigureAwait(false);
        }
        catch
        {
            _open = false;
        }
        finally
        {
            portal.SetCompleted();
            ReleasePortalOperation(portal.StartedTimestamp);
        }
    }

    private void RequestMorePortalRows(BlueTuskPortal portal) =>
        _connection.Write(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteExecute(output, portal.Name, portal.FetchSize);
                BlueTuskFrontendMessageWriter.WriteFlush(output);
            });

    private ValueTask RequestMorePortalRowsAsync(
        BlueTuskPortal portal,
        CancellationToken cancellationToken) =>
        _connection.WriteAsync(
            output =>
            {
                BlueTuskFrontendMessageWriter.WriteExecute(output, portal.Name, portal.FetchSize);
                BlueTuskFrontendMessageWriter.WriteFlush(output);
            },
            cancellationToken);

    private void CompletePortal(BlueTuskPortal portal)
    {
        _connection.Write(BlueTuskFrontendMessageWriter.WriteSync);
        DrainPortalToReady();
        portal.SetCompleted();
        ReleasePortalOperation(portal.StartedTimestamp);
    }

    private async ValueTask CompletePortalAsync(BlueTuskPortal portal)
    {
        await _connection.WriteAsync(
            BlueTuskFrontendMessageWriter.WriteSync,
            CancellationToken.None).ConfigureAwait(false);
        await DrainPortalToReadyAsync().ConfigureAwait(false);
        portal.SetCompleted();
        ReleasePortalOperation(portal.StartedTimestamp);
    }

    private void RecoverPortalError()
    {
        _connection.Write(BlueTuskFrontendMessageWriter.WriteSync);
        DrainPortalToReady();
    }

    private async ValueTask RecoverPortalErrorAsync()
    {
        await _connection.WriteAsync(
            BlueTuskFrontendMessageWriter.WriteSync,
            CancellationToken.None).ConfigureAwait(false);
        await DrainPortalToReadyAsync().ConfigureAwait(false);
    }

    private void RecoverAbandonedPortal(string portalName)
    {
        try
        {
            SkipActiveStreamedPayload();
            _connection.Write(
                output =>
                {
                    BlueTuskFrontendMessageWriter.WriteClosePortal(output, portalName);
                    BlueTuskFrontendMessageWriter.WriteSync(output);
                });
            DrainPortalToReady();
        }
        catch
        {
            _open = false;
        }
    }

    private async ValueTask RecoverAbandonedPortalAsync(string portalName)
    {
        try
        {
            await SkipActiveStreamedPayloadAsync().ConfigureAwait(false);
            await _connection.WriteAsync(
                output =>
                {
                    BlueTuskFrontendMessageWriter.WriteClosePortal(output, portalName);
                    BlueTuskFrontendMessageWriter.WriteSync(output);
                },
                CancellationToken.None).ConfigureAwait(false);
            await DrainPortalToReadyAsync().ConfigureAwait(false);
        }
        catch
        {
            _open = false;
        }
    }

    private void DrainPortalToReady()
    {
        while (true)
        {
            var header = ReadStreamedMessageHeader();
            if (header.Identifier == 'D')
            {
                SkipStreamedPayload(header.PayloadLength);
                continue;
            }

            var message = ReadStreamedMessage(header);
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    CompleteReadyForQuerySynchronously(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                    return;
                default:
                    break;
            }
        }
    }

    private async ValueTask DrainPortalToReadyAsync()
    {
        while (true)
        {
            var header = await ReadStreamedMessageHeaderAsync(CancellationToken.None).ConfigureAwait(false);
            if (header.Identifier == 'D')
            {
                await SkipStreamedPayloadAsync(header.PayloadLength).ConfigureAwait(false);
                continue;
            }

            var message = await ReadStreamedMessageAsync(
                header,
                CancellationToken.None).ConfigureAwait(false);
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    await CompleteReadyForQuery(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message)).ConfigureAwait(false);
                    return;
                default:
                    break;
            }
        }
    }

    private void SkipStreamedPayload(int count)
    {
        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0)
        {
            var chunk = Math.Min(count, scratch.Length);
            ReadPortalPayloadExactly(scratch[..chunk]);
            count -= chunk;
        }
    }

    private async ValueTask SkipStreamedPayloadAsync(int count)
    {
        var scratch = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            var chunk = Math.Min(count, scratch.Length);
            await ReadPortalPayloadExactlyAsync(
                scratch.AsMemory(0, chunk),
                CancellationToken.None).ConfigureAwait(false);
            count -= chunk;
        }
    }

    private void SkipActiveStreamedPayload()
    {
        var remaining = _connection.ActiveMessagePayloadRemaining;
        if (remaining != 0)
        {
            SkipStreamedPayload(remaining);
        }
    }

    private ValueTask SkipActiveStreamedPayloadAsync()
    {
        var remaining = _connection.ActiveMessagePayloadRemaining;
        return remaining == 0
            ? ValueTask.CompletedTask
            : SkipStreamedPayloadAsync(remaining);
    }

    private BlueTuskBackendMessage ReadStreamedMessage()
    {
        var header = ReadStreamedMessageHeader();
        return ReadStreamedMessage(header);
    }

    private BlueTuskBackendMessage ReadStreamedMessage(BlueTuskBackendMessageHeader header)
    {
        var payload = GC.AllocateUninitializedArray<byte>(header.PayloadLength);
        ReadPortalPayloadExactly(payload);
        return new BlueTuskBackendMessage(header.Code, new ReadOnlySequence<byte>(payload));
    }

    private async ValueTask<BlueTuskBackendMessage> ReadStreamedMessageAsync(
        CancellationToken cancellationToken)
    {
        var header = await ReadStreamedMessageHeaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStreamedMessageAsync(header, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BlueTuskBackendMessage> ReadStreamedMessageAsync(
        BlueTuskBackendMessageHeader header,
        CancellationToken cancellationToken)
    {
        var payload = GC.AllocateUninitializedArray<byte>(header.PayloadLength);
        await ReadPortalPayloadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new BlueTuskBackendMessage(header.Code, new ReadOnlySequence<byte>(payload));
    }

    private BlueTuskBackendMessageHeader ReadStreamedMessageHeader()
    {
        var header = _connection.ReadMessageHeader();
        BlueTuskDiagnostics.ProtocolMessageSize.Record(header.PayloadLength + 5);
        return header;
    }

    private async ValueTask<BlueTuskBackendMessageHeader> ReadStreamedMessageHeaderAsync(
        CancellationToken cancellationToken)
    {
        var header = await _connection.ReadMessageHeaderAsync(cancellationToken).ConfigureAwait(false);
        BlueTuskDiagnostics.ProtocolMessageSize.Record(header.PayloadLength + 5);
        return header;
    }

    private void EnsurePortalOperation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _portalOperationActive) == 0)
        {
            throw new InvalidOperationException("No PostgreSQL portal operation is active.");
        }
    }

    private void ReleasePortalOperation(long startedTimestamp)
    {
        if (Interlocked.Exchange(ref _portalOperationActive, 0) != 0)
        {
            BlueTuskDiagnostics.CommandDuration.Record(
                Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds);
            _operationLock.Release();
        }
    }

    private void ValidatePipeline(IReadOnlyList<BlueTuskPipelineGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            throw new ArgumentException("A PostgreSQL pipeline requires at least one synchronization group.", nameof(groups));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }

        if (!Capabilities.SupportsPipelineMode)
        {
            throw new NotSupportedException(
                $"PostgreSQL pipeline mode requires PostgreSQL 14 or later; the connected server is {Capabilities.ServerVersion}.");
        }

        foreach (var group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(group.Queries);
            if (group.Queries.Count == 0)
            {
                throw new ArgumentException(
                    "Every PostgreSQL pipeline synchronization group requires at least one query.",
                    nameof(groups));
            }

            foreach (var query in group.Queries)
            {
                ArgumentNullException.ThrowIfNull(query);
                ValidateQuery(query.Sql);
                ArgumentNullException.ThrowIfNull(query.Parameters);
            }
        }
    }

    private static void WritePipeline(
        IBufferWriter<byte> output,
        IReadOnlyList<BlueTuskPipelineGroup> groups)
    {
        foreach (var group in groups)
        {
            foreach (var query in group.Queries)
            {
                var typeOids = query.Parameters
                    .Select(static parameter => parameter.TypeOid)
                    .ToArray();
                var bindParameters = query.Parameters
                    .Select(static parameter =>
                        new BlueTuskBindParameter(parameter.FormatCode, parameter.Value))
                    .ToArray();
                BlueTuskFrontendMessageWriter.WriteParse(output, string.Empty, query.Sql, typeOids);
                BlueTuskFrontendMessageWriter.WriteBind(
                    output,
                    string.Empty,
                    string.Empty,
                    bindParameters,
                    query.UseBinaryResults ? BinaryResultFormat : TextResultFormat);
                BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
            }

            BlueTuskFrontendMessageWriter.WriteSync(output);
        }
    }

    private void BeginNextPipelineGroup() =>
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);

    private async ValueTask DrainRemainingPipelineGroupsAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            BeginNextPipelineGroup();
            _ = await ReadPipelineGroupResponseAsync().ConfigureAwait(false);
        }
    }

    private BlueTuskQueryResult ExecuteQuery(Action<IBufferWriter<byte>> writeMessages)
    {
        ArgumentNullException.ThrowIfNull(writeMessages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _operationLock.Wait();
        var started = Stopwatch.GetTimestamp();
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            _connection.Write(writeMessages);
            return ReadQueryResponse();
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    internal void WriteCopyIn(ReadOnlySpan<byte> data)
    {
        EnsureSynchronousCopyState(BlueTuskConnectionState.CopyIn);
        var payload = data.ToArray();
        _connection.Write(output => BlueTuskFrontendMessageWriter.WriteCopyData(output, payload));
        BlueTuskDiagnostics.CopyBytes.Add(
            data.Length,
            new KeyValuePair<string, object?>("direction", "in"));
    }

    internal BlueTuskCopyResult CompleteCopyIn(
        BlueTuskCopyResponse response,
        long bytesTransferred)
    {
        EnsureSynchronousCopyState(BlueTuskConnectionState.CopyIn);
        try
        {
            _connection.Write(BlueTuskFrontendMessageWriter.WriteCopyDone);
            var commandTag = ReadCopyCompletion(suppressServerError: false);
            return new BlueTuskCopyResult(response, commandTag, bytesTransferred);
        }
        catch
        {
            _open = false;
            throw;
        }
        finally
        {
            ReleaseSynchronousCopyOperation();
        }
    }

    internal void AbortCopyInOperation()
    {
        if (Volatile.Read(ref _synchronousCopyOperationActive) == 0)
        {
            return;
        }

        try
        {
            AbortCopyIn();
        }
        finally
        {
            ReleaseSynchronousCopyOperation();
        }
    }

    internal BlueTuskCopyOutEvent ReadCopyOutEvent()
    {
        EnsureSynchronousCopyState(BlueTuskConnectionState.CopyOut);
        while (true)
        {
            var message = ReadMessage();
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'd':
                    var data = BlueTuskBackendMessageDecoder.DecodeCopyData(message);
                    BlueTuskDiagnostics.CopyBytes.Add(
                        data.Length,
                        new KeyValuePair<string, object?>("direction", "out"));
                    return new BlueTuskCopyOutEvent(BlueTuskCopyOutEventKind.Data, Data: data);
                case 'c':
                    break;
                case 'C':
                    return new BlueTuskCopyOutEvent(
                        BlueTuskCopyOutEventKind.CommandComplete,
                        CommandTag: BlueTuskBackendMessageDecoder.DecodeCommandComplete(message));
                case 'E':
                    return new BlueTuskCopyOutEvent(
                        BlueTuskCopyOutEventKind.Error,
                        Error: new BlueTuskServerException(
                            BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message)));
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    CompleteReadyForQuerySynchronously(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                    ReleaseSynchronousCopyOperation();
                    return new BlueTuskCopyOutEvent(BlueTuskCopyOutEventKind.Completed);
                default:
                    break;
            }
        }
    }

    internal void AbortCopyOutOperation()
    {
        if (Volatile.Read(ref _synchronousCopyOperationActive) == 0)
        {
            return;
        }

        try
        {
            AbortCopyOut();
        }
        finally
        {
            ReleaseSynchronousCopyOperation();
        }
    }

    private void EnsureSynchronousCopyState(BlueTuskConnectionState expectedState)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _synchronousCopyOperationActive) == 0 ||
            _connection.StateMachine.State != expectedState)
        {
            throw new InvalidOperationException($"The session is not in {expectedState} mode.");
        }
    }

    private void ReleaseSynchronousCopyOperation()
    {
        if (Interlocked.Exchange(ref _synchronousCopyOperationActive, 0) != 0)
        {
            _operationLock.Release();
        }
    }

    private async ValueTask<BlueTuskQueryResult> ExecuteQueryAsync(
        Action<IBufferWriter<byte>> writeMessages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeMessages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            await _connection.WriteAsync(writeMessages, cancellationToken).ConfigureAwait(false);
            return await ReadQueryResponseWithCancellationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BlueTuskDiagnostics.CommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            _operationLock.Release();
        }
    }

    private BlueTuskCopyResult CopyInCore(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted)
    {
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
        _connection.Write(output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql));
        var response = ReadCopyStart('G');
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyIn);
        copyStarted?.Invoke(response);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long bytesTransferred = 0;
        try
        {
            while (true)
            {
                var read = source.Read(buffer, 0, CopyBufferSize);
                if (read == 0)
                {
                    break;
                }

                _connection.Write(
                    output => BlueTuskFrontendMessageWriter.WriteCopyData(
                        output,
                        buffer.AsSpan(0, read)));
                bytesTransferred = checked(bytesTransferred + read);
                BlueTuskDiagnostics.CopyBytes.Add(read, new KeyValuePair<string, object?>("direction", "in"));
            }

            _connection.Write(BlueTuskFrontendMessageWriter.WriteCopyDone);
            var commandTag = ReadCopyCompletion(suppressServerError: false);
            return new BlueTuskCopyResult(response, commandTag, bytesTransferred);
        }
        catch (Exception) when (_connection.StateMachine.State == BlueTuskConnectionState.CopyIn)
        {
            AbortCopyIn();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private BlueTuskCopyResult CopyOutCore(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted)
    {
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
        _connection.Write(output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql));
        var response = ReadCopyStart('H');
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyOut);
        copyStarted?.Invoke(response);

        long bytesTransferred = 0;
        string? commandTag = null;
        BlueTuskServerException? deferredError = null;
        try
        {
            while (true)
            {
                var message = ReadMessage();
                switch (message.Identifier)
                {
                    case 'A':
                        EnqueueNotification(message);
                        break;
                    case 'd':
                        var data = BlueTuskBackendMessageDecoder.DecodeCopyData(message);
                        destination.Write(data);
                        bytesTransferred = checked(bytesTransferred + data.Length);
                        BlueTuskDiagnostics.CopyBytes.Add(
                            data.Length,
                            new KeyValuePair<string, object?>("direction", "out"));
                        break;
                    case 'c':
                        break;
                    case 'C':
                        commandTag = BlueTuskBackendMessageDecoder.DecodeCommandComplete(message);
                        break;
                    case 'E':
                        deferredError = new BlueTuskServerException(
                            BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'Z':
                        CompleteReadyForQuerySynchronously(
                            BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                        if (deferredError is not null)
                        {
                            throw deferredError;
                        }

                        return new BlueTuskCopyResult(
                            response,
                            commandTag ?? throw new BlueTuskProtocolException(
                                "COPY OUT completed without a command tag."),
                            bytesTransferred);
                    default:
                        break;
                }
            }
        }
        catch (Exception) when (
            _connection.StateMachine.State is BlueTuskConnectionState.CopyOut or
                BlueTuskConnectionState.Cancelling)
        {
            AbortCopyOut();
            throw;
        }
    }

    private async ValueTask<BlueTuskCopyResult> CopyInCoreAsync(
        string sql,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
        await _connection.WriteAsync(
            output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql),
            CancellationToken.None).ConfigureAwait(false);
        var response = await ReadCopyStartAsync('G').ConfigureAwait(false);
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyIn);
        copyStarted?.Invoke(response);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long bytesTransferred = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, CopyBufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await _connection.WriteAsync(
                    output => BlueTuskFrontendMessageWriter.WriteCopyData(
                        output,
                        buffer.AsSpan(0, read)),
                    CancellationToken.None).ConfigureAwait(false);
                bytesTransferred = checked(bytesTransferred + read);
                BlueTuskDiagnostics.CopyBytes.Add(read, new KeyValuePair<string, object?>("direction", "in"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _connection.WriteAsync(
                BlueTuskFrontendMessageWriter.WriteCopyDone,
                CancellationToken.None).ConfigureAwait(false);
            var commandTag = await ReadCopyCompletionAsync(suppressServerError: false).ConfigureAwait(false);
            return new BlueTuskCopyResult(response, commandTag, bytesTransferred);
        }
        catch (Exception) when (_connection.StateMachine.State == BlueTuskConnectionState.CopyIn)
        {
            await AbortCopyInAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<BlueTuskCopyResult> CopyOutCoreAsync(
        string sql,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
        await _connection.WriteAsync(
            output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, sql),
            CancellationToken.None).ConfigureAwait(false);
        var response = await ReadCopyStartAsync('H').ConfigureAwait(false);
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.CopyOut);
        copyStarted?.Invoke(response);

        long bytesTransferred = 0;
        string? commandTag = null;
        BlueTuskServerException? deferredError = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = await ReadMessageAsync(CancellationToken.None).ConfigureAwait(false);
                switch (message.Identifier)
                {
                    case 'A':
                        EnqueueNotification(message);
                        break;
                    case 'd':
                        var data = BlueTuskBackendMessageDecoder.DecodeCopyData(message);
                        await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                        bytesTransferred = checked(bytesTransferred + data.Length);
                        BlueTuskDiagnostics.CopyBytes.Add(
                            data.Length,
                            new KeyValuePair<string, object?>("direction", "out"));
                        break;
                    case 'c':
                        break;
                    case 'C':
                        commandTag = BlueTuskBackendMessageDecoder.DecodeCommandComplete(message);
                        break;
                    case 'E':
                        deferredError = new BlueTuskServerException(
                            BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'Z':
                        await CompleteReadyForQuery(
                            BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message)).ConfigureAwait(false);
                        if (deferredError is not null)
                        {
                            throw deferredError;
                        }

                        return new BlueTuskCopyResult(
                            response,
                            commandTag ?? throw new BlueTuskProtocolException(
                                "COPY OUT completed without a command tag."),
                            bytesTransferred);
                    default:
                        break;
                }
            }
        }
        catch (Exception) when (
            _connection.StateMachine.State is BlueTuskConnectionState.CopyOut or
                BlueTuskConnectionState.Cancelling)
        {
            await AbortCopyOutAsync().ConfigureAwait(false);
            throw;
        }
    }

    private BlueTuskCopyResponse ReadCopyStart(char expectedIdentifier)
    {
        BlueTuskServerException? deferredError = null;
        while (true)
        {
            var message = ReadMessage();
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case var identifier when identifier == expectedIdentifier:
                    return BlueTuskBackendMessageDecoder.DecodeCopyResponse(message);
                case 'E':
                    deferredError = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    CompleteReadyForQuerySynchronously(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                    if (deferredError is not null)
                    {
                        throw deferredError;
                    }

                    throw new BlueTuskProtocolException(
                        $"PostgreSQL did not enter the expected COPY {(expectedIdentifier == 'G' ? "IN" : "OUT")} mode.");
                default:
                    break;
            }
        }
    }

    private string ReadCopyCompletion(bool suppressServerError)
    {
        string? commandTag = null;
        BlueTuskServerException? deferredError = null;
        while (true)
        {
            var message = ReadMessage();
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'c':
                    break;
                case 'C':
                    commandTag = BlueTuskBackendMessageDecoder.DecodeCommandComplete(message);
                    break;
                case 'E':
                    deferredError = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    CompleteReadyForQuerySynchronously(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                    if (!suppressServerError && deferredError is not null)
                    {
                        throw deferredError;
                    }

                    return commandTag ?? string.Empty;
                default:
                    break;
            }
        }
    }

    private async ValueTask<BlueTuskCopyResponse> ReadCopyStartAsync(char expectedIdentifier)
    {
        BlueTuskServerException? deferredError = null;
        while (true)
        {
            var message = await ReadMessageAsync(CancellationToken.None).ConfigureAwait(false);
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case var identifier when identifier == expectedIdentifier:
                    return BlueTuskBackendMessageDecoder.DecodeCopyResponse(message);
                case 'E':
                    deferredError = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    await CompleteReadyForQuery(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message)).ConfigureAwait(false);
                    if (deferredError is not null)
                    {
                        throw deferredError;
                    }

                    throw new BlueTuskProtocolException(
                        $"PostgreSQL did not enter the expected COPY {(expectedIdentifier == 'G' ? "IN" : "OUT")} mode.");
                default:
                    break;
            }
        }
    }

    private async ValueTask<string> ReadCopyCompletionAsync(bool suppressServerError)
    {
        string? commandTag = null;
        BlueTuskServerException? deferredError = null;
        while (true)
        {
            var message = await ReadMessageAsync(CancellationToken.None).ConfigureAwait(false);
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'c':
                    break;
                case 'C':
                    commandTag = BlueTuskBackendMessageDecoder.DecodeCommandComplete(message);
                    break;
                case 'E':
                    deferredError = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    await CompleteReadyForQuery(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message)).ConfigureAwait(false);
                    if (!suppressServerError && deferredError is not null)
                    {
                        throw deferredError;
                    }

                    return commandTag ?? string.Empty;
                default:
                    break;
            }
        }
    }

    internal async ValueTask<BlueTuskCopyBothReadResult> ReadCopyBothAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _copyBothOperationActive) == 0 ||
            _connection.StateMachine.State != BlueTuskConnectionState.CopyBoth)
        {
            return BlueTuskCopyBothReadResult.Completed(commandTag: null);
        }

        string? commandTag = null;
        BlueTuskServerException? deferredError = null;
        try
        {
            while (true)
            {
                var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Identifier)
                {
                    case 'd':
                        var data = BlueTuskBackendMessageDecoder.DecodeCopyData(message);
                        BlueTuskDiagnostics.CopyBytes.Add(
                            data.Length,
                            new KeyValuePair<string, object?>("direction", "both.in"));
                        return BlueTuskCopyBothReadResult.Payload(data);
                    case 'c':
                        break;
                    case 'C':
                        commandTag = BlueTuskBackendMessageDecoder.DecodeCommandComplete(message);
                        break;
                    case 'E':
                        deferredError = new BlueTuskServerException(
                            BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'A':
                        EnqueueNotification(message);
                        break;
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'Z':
                        await CompleteReadyForQuery(
                            BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message)).ConfigureAwait(false);
                        ReleaseCopyBothOperation();
                        if (deferredError is not null)
                        {
                            throw deferredError;
                        }

                        return BlueTuskCopyBothReadResult.Completed(commandTag);
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _open = false;
            ReleaseCopyBothOperation();
            throw;
        }
    }

    internal async ValueTask WriteCopyBothAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _copyBothOperationActive) == 0 ||
            _connection.StateMachine.State != BlueTuskConnectionState.CopyBoth)
        {
            throw new InvalidOperationException("The session is not in COPY BOTH mode.");
        }

        await _connection.WriteAsync(
            output => BlueTuskFrontendMessageWriter.WriteCopyData(output, data.Span),
            cancellationToken).ConfigureAwait(false);
        BlueTuskDiagnostics.CopyBytes.Add(
            data.Length,
            new KeyValuePair<string, object?>("direction", "both.out"));
    }

    internal async ValueTask<string?> CompleteCopyBothAsync()
    {
        if (Volatile.Read(ref _copyBothOperationActive) == 0)
        {
            return null;
        }

        try
        {
            if (_connection.StateMachine.State == BlueTuskConnectionState.CopyBoth)
            {
                await _connection.WriteAsync(
                    BlueTuskFrontendMessageWriter.WriteCopyDone,
                    CancellationToken.None).ConfigureAwait(false);
                return await ReadCopyCompletionAsync(suppressServerError: false).ConfigureAwait(false);
            }

            return null;
        }
        catch
        {
            _open = false;
            throw;
        }
        finally
        {
            ReleaseCopyBothOperation();
        }
    }

    private void AbortCopyIn()
    {
        if (_connection.StateMachine.State != BlueTuskConnectionState.CopyIn)
        {
            return;
        }

        try
        {
            _connection.Write(
                output => BlueTuskFrontendMessageWriter.WriteCopyFail(
                    output,
                    "The client COPY source failed."));
            _ = ReadCopyCompletion(suppressServerError: true);
        }
        catch
        {
            _open = false;
        }
    }

    private void AbortCopyOut()
    {
        if (_connection.StateMachine.State == BlueTuskConnectionState.CopyOut)
        {
            Cancel();
        }

        if (_connection.StateMachine.State == BlueTuskConnectionState.Cancelling)
        {
            try
            {
                _ = ReadCopyCompletion(suppressServerError: true);
                SynchronizeAfterCancellation();
            }
            catch
            {
                _open = false;
            }
        }
    }

    private async ValueTask AbortCopyInAsync()
    {
        if (_connection.StateMachine.State != BlueTuskConnectionState.CopyIn)
        {
            return;
        }

        try
        {
            await _connection.WriteAsync(
                output => BlueTuskFrontendMessageWriter.WriteCopyFail(
                    output,
                    "The client COPY source failed."),
                CancellationToken.None).ConfigureAwait(false);
            _ = await ReadCopyCompletionAsync(suppressServerError: true).ConfigureAwait(false);
        }
        catch
        {
            _open = false;
        }
    }

    private async ValueTask AbortCopyOutAsync()
    {
        if (_connection.StateMachine.State == BlueTuskConnectionState.CopyOut)
        {
            await CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_connection.StateMachine.State == BlueTuskConnectionState.Cancelling)
        {
            try
            {
                _ = await ReadCopyCompletionAsync(suppressServerError: true).ConfigureAwait(false);
                await SynchronizeAfterCancellationAsync().ConfigureAwait(false);
            }
            catch
            {
                _open = false;
            }
        }
    }

    private void SynchronizeAfterCancellation()
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            _connection.Write(static output =>
                BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, "SELECT 1"));
            try
            {
                _ = ReadQueryResponse();
                return;
            }
            catch (BlueTuskServerException exception) when (exception.SqlState == "57014")
            {
                // A CancelRequest can arrive after COPY itself reached ReadyForQuery. Consume it here.
            }
        }

        throw new BlueTuskProtocolException(
            "PostgreSQL continued to cancel synchronization queries after COPY cleanup.");
    }

    private async ValueTask SynchronizeAfterCancellationAsync()
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Executing);
            await _connection.WriteAsync(
                static output => BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, "SELECT 1"),
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                _ = await ReadQueryResponseAsync().ConfigureAwait(false);
                return;
            }
            catch (BlueTuskServerException exception) when (exception.SqlState == "57014")
            {
                // A CancelRequest can arrive after COPY itself reached ReadyForQuery. Consume it here.
            }
        }

        throw new BlueTuskProtocolException(
            "PostgreSQL continued to cancel synchronization queries after COPY cleanup.");
    }

    private async ValueTask<BlueTuskBackendMessage> ReadMessageAsync(
        CancellationToken cancellationToken)
    {
        var message = await _connection.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
        BlueTuskDiagnostics.ProtocolMessageSize.Record(message.Length + 5);
        return message;
    }

    private BlueTuskBackendMessage ReadMessage()
    {
        var message = _connection.ReadMessage();
        BlueTuskDiagnostics.ProtocolMessageSize.Record(message.Length + 5);
        return message;
    }

    private BlueTuskQueryResult ReadQueryResponse()
    {
        var response = ReadPipelineGroupResponse();
        if (response.Error is not null)
        {
            throw response.Error;
        }

        return response.Result;
    }

    private BlueTuskPipelineGroupResult ReadPipelineGroupResponse()
    {
        var resultSets = new List<BlueTuskResultSet>();
        IReadOnlyList<BlueTuskFieldDescription> fields = [];
        List<BlueTuskDataRow> rows = [];
        BlueTuskServerException? deferredError = null;

        while (true)
        {
            var message = ReadMessage();
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'T':
                    fields = BlueTuskBackendMessageDecoder.DecodeRowDescription(message);
                    rows = [];
                    break;
                case 'D':
                    rows.Add(BlueTuskBackendMessageDecoder.DecodeDataRow(message, fields.Count));
                    break;
                case 'C':
                    resultSets.Add(new BlueTuskResultSet(
                        fields,
                        rows,
                        BlueTuskBackendMessageDecoder.DecodeCommandComplete(message)));
                    fields = [];
                    rows = [];
                    break;
                case 'I':
                    resultSets.Add(new BlueTuskResultSet([], [], string.Empty));
                    break;
                case 'E':
                    deferredError = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    CompleteReadyForQuerySynchronously(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                    return new BlueTuskPipelineGroupResult(
                        new BlueTuskQueryResult(resultSets),
                        deferredError);
                default:
                    break;
            }
        }
    }

    private async ValueTask<BlueTuskQueryResult> ReadQueryResponseWithCancellationAsync(
        CancellationToken cancellationToken)
    {
        var responseTask = ReadQueryResponseAsync().AsTask();
        if (!cancellationToken.CanBeCanceled)
        {
            return await responseTask.ConfigureAwait(false);
        }

        try
        {
            return await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (responseTask.IsCompleted)
            {
                return await responseTask.ConfigureAwait(false);
            }

            try
            {
                await CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                ObserveFault(responseTask);
                _open = false;
                await _connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            try
            {
                _ = await responseTask.ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception) when (exception.SqlState == "57014")
            {
                // PostgreSQL confirmed that the query was cancelled; ReadyForQuery has already been consumed.
            }

            throw new OperationCanceledException("The PostgreSQL operation was cancelled.", cancellationToken);
        }
    }

    private async ValueTask<BlueTuskQueryResult> ReadQueryResponseAsync()
    {
        var response = await ReadPipelineGroupResponseAsync().ConfigureAwait(false);
        if (response.Error is not null)
        {
            throw response.Error;
        }

        return response.Result;
    }

    private async ValueTask<BlueTuskPipelineGroupResult> ReadPipelineGroupResponseAsync()
    {
        var resultSets = new List<BlueTuskResultSet>();
        IReadOnlyList<BlueTuskFieldDescription> fields = [];
        List<BlueTuskDataRow> rows = [];
        BlueTuskServerException? deferredError = null;

        while (true)
        {
            var message = await _connection.ReadMessageAsync(CancellationToken.None).ConfigureAwait(false);
            BlueTuskDiagnostics.ProtocolMessageSize.Record(message.Length + 5);
            switch (message.Identifier)
            {
                case 'A':
                    EnqueueNotification(message);
                    break;
                case 'T':
                    fields = BlueTuskBackendMessageDecoder.DecodeRowDescription(message);
                    rows = [];
                    break;
                case 'D':
                    rows.Add(BlueTuskBackendMessageDecoder.DecodeDataRow(message, fields.Count));
                    break;
                case 'C':
                    resultSets.Add(new BlueTuskResultSet(
                        fields,
                        rows,
                        BlueTuskBackendMessageDecoder.DecodeCommandComplete(message)));
                    fields = [];
                    rows = [];
                    break;
                case 'I':
                    resultSets.Add(new BlueTuskResultSet([], [], string.Empty));
                    break;
                case 'E':
                    deferredError = new BlueTuskServerException(
                        BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'N':
                    _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    break;
                case 'S':
                    StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                    break;
                case 'Z':
                    var cancellationCompletion = CompleteReadyForQuery(
                        BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message));
                    await cancellationCompletion.ConfigureAwait(false);
                    return new BlueTuskPipelineGroupResult(
                        new BlueTuskQueryResult(resultSets),
                        deferredError);
                default:
                    break;
            }
        }
    }

    private async ValueTask<BlueTuskPipelineGroupResult> ReadPipelineGroupResponseWithCancellationAsync(
        CancellationToken cancellationToken)
    {
        var responseTask = ReadPipelineGroupResponseAsync().AsTask();
        if (!cancellationToken.CanBeCanceled)
        {
            return await responseTask.ConfigureAwait(false);
        }

        try
        {
            return await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (responseTask.IsCompleted)
            {
                return await responseTask.ConfigureAwait(false);
            }

            try
            {
                await CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                ObserveFault(responseTask);
                _open = false;
                await _connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _ = await responseTask.ConfigureAwait(false);
            throw new OperationCanceledException("The PostgreSQL pipeline was cancelled.", cancellationToken);
        }
    }

    private Task CompleteReadyForQuery(BlueTuskTransactionStatus transactionStatus)
    {
        lock (_cancellationSync)
        {
            TransactionStatus = transactionStatus;
            _connection.StateMachine.TransitionTo(
                TransactionStatus == BlueTuskTransactionStatus.FailedTransaction
                    ? BlueTuskConnectionState.FailedTransaction
                    : BlueTuskConnectionState.Ready);
            return _cancellationRequest?.Task ?? Task.CompletedTask;
        }
    }

    private void CompleteReadyForQuerySynchronously(BlueTuskTransactionStatus transactionStatus)
    {
        Task cancellationCompletion;
        lock (_cancellationSync)
        {
            TransactionStatus = transactionStatus;
            _connection.StateMachine.TransitionTo(
                TransactionStatus == BlueTuskTransactionStatus.FailedTransaction
                    ? BlueTuskConnectionState.FailedTransaction
                    : BlueTuskConnectionState.Ready);
            cancellationCompletion = _cancellationRequest?.Task ?? Task.CompletedTask;
        }

        cancellationCompletion.GetAwaiter().GetResult();
    }

    private void EnqueueNotification(BlueTuskBackendMessage message) =>
        _pendingNotifications.Enqueue(
            BlueTuskBackendMessageDecoder.DecodeNotificationResponse(message));

    private void ReleaseCopyBothOperation()
    {
        if (Interlocked.Exchange(ref _copyBothOperationActive, 0) != 0)
        {
            _operationLock.Release();
        }
    }

    private TaskCompletionSource<bool>? BeginCancellation()
    {
        lock (_cancellationSync)
        {
            var state = _connection.StateMachine.State;
            if (state is not (
                    BlueTuskConnectionState.Executing or
                    BlueTuskConnectionState.CopyIn or
                    BlueTuskConnectionState.CopyOut or
                    BlueTuskConnectionState.CopyBoth) ||
                !_connection.StateMachine.TryTransition(
                    state,
                    BlueTuskConnectionState.Cancelling))
            {
                return null;
            }

            _cancellationRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _cancellationRequest;
        }
    }

    private void CompleteCancellation(TaskCompletionSource<bool> completion)
    {
        lock (_cancellationSync)
        {
            completion.TrySetResult(true);
            if (ReferenceEquals(_cancellationRequest, completion))
            {
                _cancellationRequest = null;
            }
        }
    }

    private BlueTuskBackendKeyData GetBackendKeyData() =>
        BackendKeyData ?? throw new InvalidOperationException("PostgreSQL did not provide cancellation key data.");

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void ValidateQuery(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (!_open)
        {
            throw new InvalidOperationException("The PostgreSQL session is not open.");
        }
    }

    private static void ValidatePreparedStatementName(string statementName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementName);
        if (statementName.Contains('\0'))
        {
            throw new ArgumentException(
                "Prepared statement names cannot contain a null character.",
                nameof(statementName));
        }
    }

    private async ValueTask OpenCoreAsync(CancellationToken cancellationToken)
    {
        await _connection.ConnectAsync(
            new BlueTuskEndpoint.Tcp(_options.Host, _options.Port),
            new BlueTuskTransportOptions { ConnectTimeout = _options.ConnectTimeout },
            cancellationToken).ConfigureAwait(false);
        BlueTuskDiagnostics.ConnectionsOpened.Add(1);

        byte[]? channelBindingData = null;
        if (_options.SslMode == BlueTuskSslMode.Disable)
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Startup);
        }
        else
        {
            channelBindingData = await NegotiateTlsAsync(cancellationToken).ConfigureAwait(false);
        }

        var startupParameters = new Dictionary<string, string>
        {
            ["user"] = _options.Username,
            ["database"] = _options.Database,
            ["client_encoding"] = "UTF8",
            ["application_name"] = _options.ApplicationName,
        };
        switch (_options.ReplicationMode)
        {
            case BlueTuskReplicationMode.Physical:
                startupParameters["replication"] = "true";
                break;
            case BlueTuskReplicationMode.Database:
                startupParameters["replication"] = "database";
                break;
        }

        await _connection.WriteAsync(
            output => BlueTuskFrontendMessageWriter.WriteStartupMessage(
                output,
                startupParameters),
            cancellationToken).ConfigureAwait(false);
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Authentication);

        try
        {
            await AuthenticateAndInitialiseAsync(channelBindingData, cancellationToken).ConfigureAwait(false);
            _open = true;
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(channelBindingData);
        }
    }

    private void OpenCore()
    {
        _connection.Connect(
            new BlueTuskEndpoint.Tcp(_options.Host, _options.Port),
            new BlueTuskTransportOptions { ConnectTimeout = _options.ConnectTimeout });
        BlueTuskDiagnostics.ConnectionsOpened.Add(1);

        byte[]? channelBindingData = null;
        if (_options.SslMode == BlueTuskSslMode.Disable)
        {
            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Startup);
        }
        else
        {
            channelBindingData = NegotiateTls();
        }

        var startupParameters = CreateStartupParameters();
        _connection.Write(
            output => BlueTuskFrontendMessageWriter.WriteStartupMessage(
                output,
                startupParameters));
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Authentication);

        try
        {
            AuthenticateAndInitialise(channelBindingData);
            _open = true;
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(channelBindingData);
        }
    }

    private Dictionary<string, string> CreateStartupParameters()
    {
        var startupParameters = new Dictionary<string, string>
        {
            ["user"] = _options.Username,
            ["database"] = _options.Database,
            ["client_encoding"] = "UTF8",
            ["application_name"] = _options.ApplicationName,
        };
        switch (_options.ReplicationMode)
        {
            case BlueTuskReplicationMode.Physical:
                startupParameters["replication"] = "true";
                break;
            case BlueTuskReplicationMode.Database:
                startupParameters["replication"] = "database";
                break;
        }

        return startupParameters;
    }

    private async ValueTask<byte[]?> NegotiateTlsAsync(CancellationToken cancellationToken)
    {
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.EncryptionNegotiation);
        await _connection.WriteAsync(BlueTuskFrontendMessageWriter.WriteSslRequest, cancellationToken).ConfigureAwait(false);
        var response = await _connection.ReadUnframedByteAsync(cancellationToken).ConfigureAwait(false);
        if (response == (byte)'N')
        {
            if (_options.SslMode is BlueTuskSslMode.Require or BlueTuskSslMode.VerifyFull)
            {
                throw new BlueTuskAuthenticationException("PostgreSQL refused the required TLS connection.");
            }

            if (_options.ChannelBinding == BlueTuskChannelBindingMode.Require)
            {
                throw new BlueTuskAuthenticationException("Channel binding is required, but PostgreSQL refused TLS.");
            }

            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Startup);
            return null;
        }

        if (response != (byte)'S')
        {
            throw new BlueTuskProtocolException($"PostgreSQL returned invalid SSL negotiation byte {response}.");
        }

        if (_connection.Transport is not IBlueTuskTlsTransport tlsTransport)
        {
            throw new InvalidOperationException("The configured transport cannot be upgraded to TLS.");
        }

        await tlsTransport.UpgradeToTlsAsync(
            new BlueTuskTlsOptions
            {
                TargetHost = _options.Host,
                CertificateRevocationCheckMode = _options.CertificateRevocationCheckMode,
                RemoteCertificateValidationCallback = _options.RemoteCertificateValidationCallback,
            },
            cancellationToken).ConfigureAwait(false);
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Startup);

        return _options.ChannelBinding == BlueTuskChannelBindingMode.Disable || tlsTransport.RemoteCertificate is null
            ? null
            : BlueTuskTlsServerEndPoint.Create(tlsTransport.RemoteCertificate);
    }

    private byte[]? NegotiateTls()
    {
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.EncryptionNegotiation);
        _connection.Write(BlueTuskFrontendMessageWriter.WriteSslRequest);
        var response = _connection.ReadUnframedByte();
        if (response == (byte)'N')
        {
            if (_options.SslMode is BlueTuskSslMode.Require or BlueTuskSslMode.VerifyFull)
            {
                throw new BlueTuskAuthenticationException("PostgreSQL refused the required TLS connection.");
            }

            if (_options.ChannelBinding == BlueTuskChannelBindingMode.Require)
            {
                throw new BlueTuskAuthenticationException("Channel binding is required, but PostgreSQL refused TLS.");
            }

            _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Startup);
            return null;
        }

        if (response != (byte)'S')
        {
            throw new BlueTuskProtocolException($"PostgreSQL returned invalid SSL negotiation byte {response}.");
        }

        if (_connection.Transport is not IBlueTuskTlsTransport tlsTransport)
        {
            throw new InvalidOperationException("The configured transport cannot be upgraded to TLS.");
        }

        tlsTransport.UpgradeToTls(
            new BlueTuskTlsOptions
            {
                TargetHost = _options.Host,
                CertificateRevocationCheckMode = _options.CertificateRevocationCheckMode,
                RemoteCertificateValidationCallback = _options.RemoteCertificateValidationCallback,
            });
        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Startup);

        return _options.ChannelBinding == BlueTuskChannelBindingMode.Disable || tlsTransport.RemoteCertificate is null
            ? null
            : BlueTuskTlsServerEndPoint.Create(tlsTransport.RemoteCertificate);
    }

    private async ValueTask AuthenticateAndInitialiseAsync(
        ReadOnlyMemory<byte>? channelBindingData,
        CancellationToken cancellationToken)
    {
        BlueTuskScramSha256Client? scram = null;
        var authenticationComplete = false;
        try
        {
            while (true)
            {
                var message = await _connection.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                BlueTuskDiagnostics.ProtocolMessageSize.Record(message.Length + 5);
                switch (message.Identifier)
                {
                    case 'R':
                        var request = BlueTuskBackendMessageDecoder.DecodeAuthentication(message);
                        switch (request)
                        {
                            case BlueTuskAuthenticationRequest.Sasl sasl:
                                scram = CreateScramClient(sasl.Mechanisms, channelBindingData);
                                await _connection.WriteAsync(
                                    output => BlueTuskFrontendMessageWriter.WriteSaslInitialResponse(
                                        output,
                                        scram.Mechanism,
                                        scram.ClientFirstMessage),
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case BlueTuskAuthenticationRequest.SaslContinue continuation when scram is not null:
                                var clientFinal = scram.CreateClientFinalMessage(continuation.Data);
                                await _connection.WriteAsync(
                                    output => BlueTuskFrontendMessageWriter.WriteSaslResponse(output, clientFinal),
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            case BlueTuskAuthenticationRequest.SaslFinal finalResponse when scram is not null:
                                scram.VerifyServerFinalMessage(finalResponse.Data);
                                break;
                            case BlueTuskAuthenticationRequest.Ok:
                                scram?.EnsureVerified();
                                authenticationComplete = true;
                                _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Initialising);
                                break;
                            default:
                                throw new BlueTuskAuthenticationException(
                                    $"PostgreSQL requested an authentication method that BlueTusk does not support yet.");
                        }

                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'K':
                        BackendKeyData = BlueTuskBackendMessageDecoder.DecodeBackendKeyData(message);
                        break;
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'E':
                        throw new BlueTuskServerException(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    case 'Z':
                        if (!authenticationComplete)
                        {
                            throw new BlueTuskProtocolException("PostgreSQL became ready before authentication completed.");
                        }

                        TransactionStatus = BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message);
                        Capabilities = BlueTuskServerCapabilities.Detect(_parameters);
                        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Ready);
                        return;
                    default:
                        break;
                }
            }
        }
        finally
        {
            scram?.Dispose();
        }
    }

    private void AuthenticateAndInitialise(ReadOnlyMemory<byte>? channelBindingData)
    {
        BlueTuskScramSha256Client? scram = null;
        var authenticationComplete = false;
        try
        {
            while (true)
            {
                var message = ReadMessage();
                switch (message.Identifier)
                {
                    case 'R':
                        var request = BlueTuskBackendMessageDecoder.DecodeAuthentication(message);
                        switch (request)
                        {
                            case BlueTuskAuthenticationRequest.Sasl sasl:
                                scram = CreateScramClient(sasl.Mechanisms, channelBindingData);
                                _connection.Write(
                                    output => BlueTuskFrontendMessageWriter.WriteSaslInitialResponse(
                                        output,
                                        scram.Mechanism,
                                        scram.ClientFirstMessage));
                                break;
                            case BlueTuskAuthenticationRequest.SaslContinue continuation when scram is not null:
                                var clientFinal = scram.CreateClientFinalMessage(continuation.Data);
                                _connection.Write(
                                    output => BlueTuskFrontendMessageWriter.WriteSaslResponse(output, clientFinal));
                                break;
                            case BlueTuskAuthenticationRequest.SaslFinal finalResponse when scram is not null:
                                scram.VerifyServerFinalMessage(finalResponse.Data);
                                break;
                            case BlueTuskAuthenticationRequest.Ok:
                                scram?.EnsureVerified();
                                authenticationComplete = true;
                                _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Initialising);
                                break;
                            default:
                                throw new BlueTuskAuthenticationException(
                                    "PostgreSQL requested an authentication method that BlueTusk does not support yet.");
                        }

                        break;
                    case 'S':
                        StoreParameter(BlueTuskBackendMessageDecoder.DecodeParameterStatus(message));
                        break;
                    case 'K':
                        BackendKeyData = BlueTuskBackendMessageDecoder.DecodeBackendKeyData(message);
                        break;
                    case 'N':
                        _notices.Add(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                        break;
                    case 'E':
                        throw new BlueTuskServerException(BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message));
                    case 'Z':
                        if (!authenticationComplete)
                        {
                            throw new BlueTuskProtocolException("PostgreSQL became ready before authentication completed.");
                        }

                        TransactionStatus = BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message);
                        Capabilities = BlueTuskServerCapabilities.Detect(_parameters);
                        _connection.StateMachine.TransitionTo(BlueTuskConnectionState.Ready);
                        return;
                    default:
                        break;
                }
            }
        }
        finally
        {
            scram?.Dispose();
        }
    }

    private BlueTuskScramSha256Client CreateScramClient(
        IReadOnlyList<string> mechanisms,
        ReadOnlyMemory<byte>? channelBindingData)
    {
        var supportsPlus = mechanisms.Contains(BlueTuskScramSha256Client.PlusMechanismName, StringComparer.Ordinal);
        var supportsStandard = mechanisms.Contains(BlueTuskScramSha256Client.MechanismName, StringComparer.Ordinal);
        if (channelBindingData is not null && supportsPlus)
        {
            return new BlueTuskScramSha256Client(
                _options.Username,
                _options.Password,
                channelBindingData: channelBindingData);
        }

        if (_options.ChannelBinding == BlueTuskChannelBindingMode.Require)
        {
            throw new BlueTuskAuthenticationException(
                "Channel binding is required, but PostgreSQL did not offer SCRAM-SHA-256-PLUS.");
        }

        return supportsStandard
            ? new BlueTuskScramSha256Client(_options.Username, _options.Password)
            : throw new BlueTuskAuthenticationException("PostgreSQL did not offer a supported SCRAM mechanism.");
    }

    private void StoreParameter(BlueTuskParameterStatus parameter) =>
        _parameters[parameter.Name] = parameter.Value;
}
