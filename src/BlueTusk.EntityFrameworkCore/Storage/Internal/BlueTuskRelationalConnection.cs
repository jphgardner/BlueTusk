using System.Data.Common;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskRelationalConnection(RelationalConnectionDependencies dependencies)
    : RelationalConnection(dependencies)
{
    protected override DbConnection CreateDbConnection()
        => new BlueTuskConnection(ConnectionString ?? string.Empty);
}
