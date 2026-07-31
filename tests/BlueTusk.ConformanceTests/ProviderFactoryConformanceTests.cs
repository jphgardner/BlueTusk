using System.Data;
using System.Data.Common;
using BlueTusk.Data;

namespace BlueTusk.ConformanceTests;

public sealed class ProviderFactoryConformanceTests
{
    [Fact]
    public void Factory_creates_every_supported_base_class_surface()
    {
        var factory = BlueTuskProviderFactory.Instance;

        Assert.True(factory.CanCreateBatch);
        Assert.IsType<BlueTuskConnection>(factory.CreateConnection());
        Assert.IsType<BlueTuskCommand>(factory.CreateCommand());
        Assert.IsType<BlueTuskParameter>(factory.CreateParameter());
        Assert.IsType<BlueTuskBatch>(factory.CreateBatch());
        Assert.IsType<BlueTuskBatchCommand>(factory.CreateBatchCommand());
        Assert.IsType<BlueTuskConnectionStringBuilder>(factory.CreateConnectionStringBuilder());
        using var dataSource = Assert.IsType<BlueTuskDataSource>(
            factory.CreateDataSource("Host=localhost;Database=postgres;Username=postgres;Password=secret"));
    }

    [Fact]
    public void Connection_reports_its_factory_and_creates_associated_commands_and_batches()
    {
        using var connection = new BlueTuskConnection(
            "Host=localhost;Database=postgres;Username=postgres;Password=secret");

        Assert.Same(BlueTuskProviderFactory.Instance, DbProviderFactories.GetFactory(connection));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal("postgres", connection.Database);
        Assert.Equal("localhost", connection.DataSource);
        Assert.True(connection.CanCreateBatch);

        using var command = connection.CreateCommand();
        Assert.Same(connection, command.Connection);
        Assert.IsType<BlueTuskParameter>(command.CreateParameter());

        using var batch = connection.CreateBatch();
        Assert.Same(connection, batch.Connection);
        Assert.IsType<BlueTuskBatchCommand>(batch.CreateBatchCommand());
    }

    [Fact]
    public void Provider_can_be_registered_and_resolved_by_invariant_name()
    {
        var invariantName = $"BlueTusk.Conformance.{Guid.NewGuid():N}";
        try
        {
            DbProviderFactories.RegisterFactory(invariantName, BlueTuskProviderFactory.Instance);

            Assert.Same(BlueTuskProviderFactory.Instance, DbProviderFactories.GetFactory(invariantName));
            Assert.Contains(
                DbProviderFactories.GetFactoryClasses().Rows.Cast<DataRow>(),
                row => string.Equals(row["InvariantName"] as string, invariantName, StringComparison.Ordinal));
        }
        finally
        {
            Assert.True(DbProviderFactories.UnregisterFactory(invariantName));
        }
    }

    [Fact]
    public void Parameter_collection_obeys_dbparametercollection_contracts()
    {
        using var command = new BlueTuskCommand();
        DbParameter first = command.CreateParameter();
        first.ParameterName = "first";
        first.DbType = DbType.Int32;
        first.Value = 1;
        DbParameter second = command.CreateParameter();
        second.ParameterName = "second";
        second.Value = "two";

        Assert.Equal(0, command.Parameters.Add(first));
        command.Parameters.AddRange(new[] { second });
        Assert.Equal(2, command.Parameters.Count);
        Assert.True(command.Parameters.Contains("FIRST"));
        Assert.Equal(1, command.Parameters.IndexOf("second"));
        Assert.Same(first, command.Parameters[0]);

        command.Parameters.RemoveAt("first");
        Assert.Single(command.Parameters.Cast<DbParameter>());
        command.Parameters.Clear();
        Assert.Empty(command.Parameters.Cast<DbParameter>());
    }

    [Fact]
    public void Unsupported_base_class_operations_fail_explicitly()
    {
        using var connection = new BlueTuskConnection();
        using var command = connection.CreateCommand();

        Assert.Throws<NotSupportedException>(() => connection.ChangeDatabase("other"));
        Assert.Throws<NotSupportedException>(() => command.CommandType = CommandType.StoredProcedure);
        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandTimeout = -1);
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
    }
}
