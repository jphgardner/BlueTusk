#if !NET7_0_OR_GREATER
global using DbDataSource = BlueTusk.Data.Compatibility.BlueTuskLegacyDataSource;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data.Compatibility;

/// <summary>Provides data-source operations on runtimes predating <c>DbDataSource</c>.</summary>
public abstract class BlueTuskLegacyDataSource : IDisposable, IAsyncDisposable
{
    public abstract string ConnectionString { get; }

    public DbConnection CreateConnection() => CreateDbConnection();

    public DbConnection OpenConnection() => OpenDbConnection();

    public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) => OpenDbConnectionAsync(cancellationToken);

    public DbCommand CreateCommand(string? commandText = null) => CreateDbCommand(commandText);

    public DbBatch CreateBatch() => CreateDbBatch();

    protected abstract DbConnection CreateDbConnection();

    protected abstract DbConnection OpenDbConnection();

    protected abstract ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default);

    protected abstract DbCommand CreateDbCommand([AllowNull] string commandText);

    protected abstract DbBatch CreateDbBatch();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    protected virtual ValueTask DisposeAsyncCore() => default;
}
#endif
