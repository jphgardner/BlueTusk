using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Creates BlueTusk connections and owns their physical connection pool.</summary>
public sealed class BlueTuskDataSource : DbDataSource
{
    private readonly BlueTuskConnectionStringBuilder _settings;
    private readonly BlueTuskConnectionPool? _pool;
    private readonly BlueTuskTypeMetadataCache _typeMetadata;

    internal BlueTuskDataSource(string connectionString, BlueTuskTypeRegistry? configuredTypes = null)
    {
        _settings = new BlueTuskConnectionStringBuilder(connectionString);
        _settings.Validate();
        ConnectionString = connectionString;
        _typeMetadata = new BlueTuskTypeMetadataCache(configuredTypes);
        if (_settings.Pooling)
        {
            _pool = new BlueTuskConnectionPool(_settings);
        }
    }

    public override string ConnectionString { get; }

    public static BlueTuskDataSource Create(string connectionString) => new(connectionString);

    public BlueTuskTypeRegistry TypeRegistry => _typeMetadata.Registry;

    public new BlueTuskConnection CreateConnection() => (BlueTuskConnection)base.CreateConnection();

    public new async ValueTask<BlueTuskConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        (BlueTuskConnection)await base.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public new BlueTuskCommand CreateCommand(string commandText) => (BlueTuskCommand)base.CreateCommand(commandText);

    public new BlueTuskBatch CreateBatch() => (BlueTuskBatch)base.CreateBatch();

    public BlueTuskPoolStatistics GetPoolStatistics() =>
        _pool?.Statistics ?? new BlueTuskPoolStatistics(
            PoolingEnabled: false,
            MinimumSize: 0,
            MaximumSize: 0,
            Total: 0,
            Idle: 0,
            Busy: 0,
            Waiting: 0,
            Opened: 0,
            Reused: 0,
            Discarded: 0);

    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
        _pool?.WarmUpAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask ReloadTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await _typeMetadata.ReloadAsync(connection.Session, cancellationToken).ConfigureAwait(false);
    }

    public void ClearPool() => _pool?.Clear();

    public ValueTask ClearPoolAsync() => _pool?.ClearAsync() ?? ValueTask.CompletedTask;

    protected override DbConnection CreateDbConnection() =>
        new BlueTuskConnection(ConnectionString, _pool, _typeMetadata);

    protected override DbConnection OpenDbConnection() =>
        throw new NotSupportedException("Synchronous connection opening is not implemented yet. Use OpenConnectionAsync.");

    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override DbCommand CreateDbCommand([AllowNull] string commandText) =>
        new BlueTuskCommand(commandText ?? string.Empty, this);

    protected override DbBatch CreateDbBatch() => new BlueTuskBatch(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pool?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_pool is not null)
        {
            await _pool.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
