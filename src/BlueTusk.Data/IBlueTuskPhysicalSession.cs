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

    void Cancel();

    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

internal sealed class BlueTuskPhysicalSession : IBlueTuskPhysicalSession
{
    private readonly BlueTuskSession _session;

    private BlueTuskPhysicalSession(BlueTuskSession session)
    {
        _session = session;
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
        return new BlueTuskPhysicalSession(session);
    }

    public ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
        _session.ExecuteSimpleQueryAsync(sql, cancellationToken);

    public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken = default) =>
        _session.ExecuteExtendedQueryAsync(sql, parameters, useBinaryResults, cancellationToken);

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

    public void Cancel() => _session.Cancel();

    public ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
        _session.CancelAsync(cancellationToken);

    public void Dispose() => _session.Dispose();

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
