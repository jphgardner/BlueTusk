using System.Data;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskTransactionTests
{
    [Theory]
    [InlineData(IsolationLevel.Unspecified, "BEGIN")]
    [InlineData(IsolationLevel.ReadUncommitted, "BEGIN ISOLATION LEVEL READ UNCOMMITTED")]
    [InlineData(IsolationLevel.ReadCommitted, "BEGIN ISOLATION LEVEL READ COMMITTED")]
    [InlineData(IsolationLevel.RepeatableRead, "BEGIN ISOLATION LEVEL REPEATABLE READ")]
    [InlineData(IsolationLevel.Serializable, "BEGIN ISOLATION LEVEL SERIALIZABLE")]
    public void Maps_supported_isolation_levels(IsolationLevel isolationLevel, string expected)
    {
        Assert.Equal(expected, BlueTuskTransaction.GetBeginStatement(isolationLevel));
    }

    [Theory]
    [InlineData(IsolationLevel.Chaos)]
    [InlineData(IsolationLevel.Snapshot)]
    public void Rejects_isolation_levels_PostgreSql_cannot_provide(IsolationLevel isolationLevel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlueTuskTransaction.GetBeginStatement(isolationLevel));
    }
}
