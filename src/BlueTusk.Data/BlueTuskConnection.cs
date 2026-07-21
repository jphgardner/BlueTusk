using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;

namespace BlueTusk.Data;

/// <summary>Represents one physical connection to PostgreSQL.</summary>
public sealed class BlueTuskConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private BlueTuskConnectionStringBuilder _settings = new();
    private BlueTuskSession? _session;
    private ConnectionState _state = ConnectionState.Closed;

    public BlueTuskConnection()
    {
    }

    public BlueTuskConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("The connection string cannot change while the connection is open.");
            }

            _settings = new BlueTuskConnectionStringBuilder(value ?? string.Empty);
            _connectionString = value ?? string.Empty;
        }
    }

    public override string Database => _settings.Database;

    public override string DataSource => _settings.Host;

    public override string ServerVersion =>
        _session?.Parameters.TryGetValue("server_version", out var version) == true ? version : string.Empty;

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => checked((int)_settings.Timeout.TotalSeconds);

    internal BlueTuskSession Session =>
        _session ?? throw new InvalidOperationException("The connection is not open.");

    public override void Open() =>
        throw new NotSupportedException("Synchronous connection opening is not implemented yet. Use OpenAsync.");

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open or opening.");
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        SetState(ConnectionState.Connecting);
        try
        {
            _session = await BlueTuskSession.OpenAsync(
                new BlueTuskClientOptions
                {
                    Host = _settings.Host,
                    Port = _settings.Port,
                    Database = _settings.Database,
                    Username = _settings.Username,
                    Password = _settings.Password,
                    ApplicationName = _settings.ApplicationName,
                    ConnectTimeout = _settings.Timeout,
                    SslMode = _settings.SslMode,
                    ChannelBinding = _settings.ChannelBinding,
                },
                cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Open);
        }
        catch
        {
            _session = null;
            SetState(ConnectionState.Closed);
            throw;
        }
    }

    public override void Close()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
        SetState(ConnectionState.Closed);
    }

    public override async Task CloseAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        SetState(ConnectionState.Closed);
    }

    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("Changing databases requires opening a new PostgreSQL connection.");

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Transactions are planned for milestone 0.0.4.");

    protected override DbCommand CreateDbCommand() => new BlueTuskCommand { Connection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void SetState(ConnectionState state)
    {
        var previous = _state;
        _state = state;
        if (previous != state)
        {
            OnStateChange(new StateChangeEventArgs(previous, state));
        }
    }
}
