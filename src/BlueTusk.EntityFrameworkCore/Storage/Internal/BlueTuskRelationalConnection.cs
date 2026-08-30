using System.Data.Common;
using BlueTusk.Data.Internal;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskRelationalConnection : RelationalConnection
{
    private readonly IProviderServices _providerServices;
    private readonly IProviderDataSource? _dataSource;

    public BlueTuskRelationalConnection(
        RelationalConnectionDependencies dependencies,
        IProviderServices providerServices)
        : base(dependencies)
    {
        _providerServices = providerServices;
        _dataSource = dependencies.ContextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource;
    }

    internal IProviderDataSource? DataSource => _dataSource;

    protected override DbConnection CreateDbConnection()
    {
        var connection = _dataSource?.CreateConnection()
            ?? _providerServices.CreateConnection(ConnectionString ?? string.Empty);
        var providerConnection = _providerServices.GetConnection(connection);
        providerConnection.AllowPendingPoolResetOnOpen();
        providerConnection.PreferExtendedProtocol();
        providerConnection.PreferBinaryResults();

        return connection;
    }
}
