using BlueTusk.Client;
using BlueTusk.Data.Internal;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskUpdateResultTests
{
    [Fact]
    public void Dml_counts_are_per_statement_without_changing_public_batch_totals()
    {
        using var reader = new BlueTuskDataReader(new BlueTuskQueryResult([
            new BlueTuskResultSet([], [], "INSERT 0 2"),
            new BlueTuskResultSet([], [], "UPDATE 0"),
            new BlueTuskResultSet([], [], "DELETE 1"),
            new BlueTuskResultSet([], [], "SELECT 1"),
        ]), null, BlueTuskBuiltInTypes.CreateRegistry());
        var metadata = (IProviderUpdateResult)reader;
        Assert.Equal(3, reader.RecordsAffected);
        Assert.False(reader.Read());
        Assert.Equal(2, metadata.CurrentStatementRowsAffected);
        Assert.True(reader.NextResult());
        Assert.Equal(0, metadata.CurrentStatementRowsAffected);
        Assert.True(reader.NextResult());
        Assert.Equal(1, metadata.CurrentStatementRowsAffected);
        Assert.True(reader.NextResult());
        Assert.Throws<InvalidOperationException>(() => metadata.CurrentStatementRowsAffected);
        reader.Close();
        Assert.Throws<InvalidOperationException>(() => metadata.CurrentStatementRowsAffected);
    }

    [Theory]
    [InlineData("COPY 1")]
    [InlineData("CREATE TABLE")]
    [InlineData("BEGIN")]
    [InlineData("UPDATE 2147483648")]
    public void Non_dml_or_unrepresentable_counts_cannot_confirm_an_ef_write(string tag)
    {
        using var reader = new BlueTuskDataReader(new BlueTuskQueryResult([
            new BlueTuskResultSet([], [], tag),
        ]), null, BlueTuskBuiltInTypes.CreateRegistry());
        Assert.Throws<InvalidOperationException>(() => ((IProviderUpdateResult)reader).CurrentStatementRowsAffected);
    }
}
