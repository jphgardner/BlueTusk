namespace BlueTusk.Data.Tests;

public sealed class BlueTuskSqlIdentifierTests
{
    [Theory]
    [InlineData("orders", "\"orders\"")]
    [InlineData("Order Events", "\"Order Events\"")]
    [InlineData("order\"events", "\"order\"\"events\"")]
    [InlineData("select", "\"select\"")]
    public void Quotes_postgresql_identifiers(string identifier, string expected)
    {
        Assert.Equal(expected, BlueTuskSqlIdentifier.Quote(identifier, nameof(identifier)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders\0events")]
    public void Rejects_identifiers_that_cannot_be_sent(string identifier)
    {
        Assert.Throws<ArgumentException>(
            () => BlueTuskSqlIdentifier.Quote(identifier, nameof(identifier)));
    }
}
