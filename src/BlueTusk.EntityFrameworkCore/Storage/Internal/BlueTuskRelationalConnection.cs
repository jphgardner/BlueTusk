using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskRelationalConnection : RelationalConnection
{
    private readonly BlueTuskDataSource? _dataSource;

    public BlueTuskRelationalConnection(RelationalConnectionDependencies dependencies)
        : base(dependencies)
    {
        _dataSource = dependencies.ContextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource;
    }

    protected override DbConnection CreateDbConnection()
        => _dataSource?.CreateConnection()
            ?? new BlueTuskConnection(ConnectionString ?? string.Empty);
}
