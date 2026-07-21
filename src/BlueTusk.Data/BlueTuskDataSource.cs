using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data;

/// <summary>Creates BlueTusk connections and buffered commands.</summary>
/// <remarks>Connection pooling will be added in milestone 0.0.5.</remarks>
public sealed class BlueTuskDataSource : DbDataSource
{
    internal BlueTuskDataSource(string connectionString)
    {
        _ = new BlueTuskConnectionStringBuilder(connectionString);
        ConnectionString = connectionString;
    }

    public override string ConnectionString { get; }

    public static BlueTuskDataSource Create(string connectionString) => new(connectionString);

    public new BlueTuskConnection CreateConnection() => (BlueTuskConnection)base.CreateConnection();

    public new async ValueTask<BlueTuskConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        (BlueTuskConnection)await base.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public new BlueTuskCommand CreateCommand(string commandText) => (BlueTuskCommand)base.CreateCommand(commandText);

    protected override DbConnection CreateDbConnection() => new BlueTuskConnection(ConnectionString);

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
}
