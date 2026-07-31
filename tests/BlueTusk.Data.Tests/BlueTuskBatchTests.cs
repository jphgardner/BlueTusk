using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskBatchTests
{
    [Fact]
    public void Provider_factory_creates_typed_batch_objects()
    {
        var factory = BlueTuskProviderFactory.Instance;

        Assert.True(factory.CanCreateBatch);
        Assert.IsType<BlueTuskBatch>(factory.CreateBatch());
        Assert.IsType<BlueTuskBatchCommand>(factory.CreateBatchCommand());
        Assert.IsType<BlueTuskParameter>(factory.CreateBatchCommand()!.CreateParameter());
    }

    [Fact]
    public void Batch_command_collection_supports_typed_mutation()
    {
        var commands = new BlueTuskBatchCommandCollection();
        var first = commands.Add("SELECT 1");
        var second = new BlueTuskBatchCommand("SELECT 2");

        commands.Add(second);
        commands.Insert(1, new BlueTuskBatchCommand("SELECT 3"));

        Assert.Equal(3, commands.Count);
        Assert.Same(first, commands[0]);
        Assert.Same(second, commands[2]);
        Assert.True(commands.Remove(second));
        Assert.Equal(2, commands.Count);
        commands.Clear();
        Assert.Empty(commands);
    }

    [Fact]
    public void Batch_validates_timeout_connection_and_command_type()
    {
        var batch = new BlueTuskBatch();
        var command = new BlueTuskBatchCommand();

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Timeout = -1);
        Assert.Throws<ArgumentException>(
            () => ((DbBatch)batch).Connection = new FakeConnection());
        Assert.Throws<NotSupportedException>(() => command.CommandType = CommandType.StoredProcedure);
        Assert.Equal(-1, command.RecordsAffected);
    }

    private sealed class FakeConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
