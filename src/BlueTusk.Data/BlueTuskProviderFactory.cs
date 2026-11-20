using System.Data.Common;

namespace BlueTusk.Data;

public sealed class BlueTuskProviderFactory : DbProviderFactory
{
    public static BlueTuskProviderFactory Instance { get; } = new();

#if NET6_0_OR_GREATER
    public override bool CanCreateBatch => true;
#else
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Retain the instance provider-factory API where the BCL lacks this member.")]
    public bool CanCreateBatch => true;
#endif

    private BlueTuskProviderFactory()
    {
    }

    public override DbConnection CreateConnection() => new BlueTuskConnection();

    public override DbCommand CreateCommand() => new BlueTuskCommand();

    public override DbParameter CreateParameter() => new BlueTuskParameter();

#if NET6_0_OR_GREATER
    public override DbBatch CreateBatch() => new BlueTuskBatch();
    public override DbBatchCommand CreateBatchCommand() => new BlueTuskBatchCommand();
#else
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Retain the instance provider-factory API where the BCL lacks this member.")]
    public DbBatch CreateBatch() => new BlueTuskBatch();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Retain the instance provider-factory API where the BCL lacks this member.")]
    public DbBatchCommand CreateBatchCommand() => new BlueTuskBatchCommand();
#endif

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new BlueTuskConnectionStringBuilder();

#if NET7_0_OR_GREATER
    public override DbDataSource CreateDataSource(string connectionString) =>
#else
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Retain the instance provider-factory API where the BCL lacks this member.")]
    public DbDataSource CreateDataSource(string connectionString) =>
#endif
        BlueTuskDataSource.Create(connectionString);
}
