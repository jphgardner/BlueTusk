using BlueTusk.Client;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskSqlTests
{
    [Theory]
    [InlineData("orders", "\"orders\"")]
    [InlineData("Order Events", "\"Order Events\"")]
    [InlineData("order\"events", "\"order\"\"events\"")]
    [InlineData("select", "\"select\"")]
    public void Quotes_postgresql_identifiers(string identifier, string expected)
    {
        Assert.Equal(expected, BlueTuskSql.QuoteIdentifier(identifier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders\0events")]
    public void Rejects_identifiers_that_cannot_be_sent(string identifier)
    {
        Assert.Throws<ArgumentException>(
            () => BlueTuskSql.QuoteIdentifier(identifier));
    }

    [Theory]
    [InlineData("orders", "E'orders'")]
    [InlineData("order's", "E'order''s'")]
    [InlineData("a\\b", "E'a\\\\b'")]
    public void Quotes_postgresql_string_literals(string value, string expected)
    {
        Assert.Equal(expected, BlueTuskSql.QuoteLiteral(value));
    }
}
