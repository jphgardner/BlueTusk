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
    private readonly BlueTuskProtocolConnection _connection;
    private readonly BlueTuskClientOptions _options;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly List<BlueTuskError> _notices = [];
    private readonly object _cancellationSync = new();
    private TaskCompletionSource<bool>? _cancellationRequest;
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

    public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
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
                    bindParameters);
                BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
                BlueTuskFrontendMessageWriter.WriteSync(output);
            },
            cancellationToken);
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
        _operationLock.Dispose();
        _connection.Dispose();
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
                    if (deferredError is not null)
                    {
                        throw deferredError;
                    }

                    return new BlueTuskQueryResult(resultSets);
                default:
                    break;
            }
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

    private TaskCompletionSource<bool>? BeginCancellation()
    {
        lock (_cancellationSync)
        {
            if (!_connection.StateMachine.TryTransition(
                    BlueTuskConnectionState.Executing,
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

        await _connection.WriteAsync(
            output => BlueTuskFrontendMessageWriter.WriteStartupMessage(
                output,
                new Dictionary<string, string>
                {
                    ["user"] = _options.Username,
                    ["database"] = _options.Database,
                    ["client_encoding"] = "UTF8",
                    ["application_name"] = _options.ApplicationName,
                }),
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
