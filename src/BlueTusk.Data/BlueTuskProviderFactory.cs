using System.Data.Common;

namespace BlueTusk.Data;

public sealed class BlueTuskProviderFactory : DbProviderFactory
{
    public static BlueTuskProviderFactory Instance { get; } = new();

    private BlueTuskProviderFactory()
    {
    }

    public override DbConnection CreateConnection() => new BlueTuskConnection();

    public override DbCommand CreateCommand() => new BlueTuskCommand();

    public override DbParameter CreateParameter() => new BlueTuskParameter();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new BlueTuskConnectionStringBuilder();
}

