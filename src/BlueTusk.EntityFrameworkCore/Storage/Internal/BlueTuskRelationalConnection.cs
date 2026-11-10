using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskRelationalConnection : RelationalConnection
{
    private readonly IProviderServices _providerServices;
    private readonly IProviderDataSource? _dataSource;
    private readonly bool _requiresSqlPgq;

    public BlueTuskRelationalConnection(
        RelationalConnectionDependencies dependencies,
        IProviderServices providerServices)
        : base(dependencies)
    {
        _providerServices = providerServices;
        _dataSource = dependencies.ContextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource;
        _requiresSqlPgq = BlueTuskOptionsExtension.GetGraphBackend(dependencies.ContextOptions)
            == BlueTuskPropertyGraphBackend.NativeSqlPgq;
    }

    internal IProviderDataSource? DataSource => _dataSource;

    public override bool Open(bool errorsExpected = false)
    {
        var opened = base.Open(errorsExpected);
        if (_requiresSqlPgq && !SupportsSqlPgq())
        {
            Close();
            throw MissingSqlPgq();
        }

        return opened;
    }

    public override async Task<bool> OpenAsync(CancellationToken cancellationToken, bool errorsExpected = false)
    {
        var opened = await base.OpenAsync(cancellationToken, errorsExpected).ConfigureAwait(false);
        if (_requiresSqlPgq && !SupportsSqlPgq())
        {
            await CloseAsync().ConfigureAwait(false);
            throw MissingSqlPgq();
        }

        return opened;
    }

    private bool SupportsSqlPgq() => _providerServices.GetConnection(DbConnection).Capabilities?.SupportsSqlPgq == true;

    private static BlueTuskGraphTranslationException MissingSqlPgq() =>
        new("Native SQL/PGQ was selected in EF, but the PostgreSQL connection does not support it.");

    protected override DbConnection CreateDbConnection()
    {
        var connection = _dataSource?.CreateConnection()
            ?? _providerServices.CreateConnection(ConnectionString ?? string.Empty);
        ConfigureQueryConnection(_providerServices.GetConnection(connection));
        return connection;
    }

    internal static BlueTuskConnection CreateScopedReadConnection(BlueTuskDataSource dataSource)
    {
        var connection = dataSource.CreateConnection();
        ConfigureQueryConnection(connection);
        return connection;
    }

    internal static BlueTuskCommand CreateBufferedScopedReadCommand(BlueTuskConnection connection)
    {
        var command = (BlueTuskCommand)connection.CreateCommand();
        command.BufferResults = true;
        return command;
    }

    private static void ConfigureQueryConnection(IProviderConnection providerConnection)
    {
        providerConnection.AllowPendingPoolResetOnOpen();
        providerConnection.PreferExtendedProtocol();
        providerConnection.PreferBinaryResults();
    }
}
