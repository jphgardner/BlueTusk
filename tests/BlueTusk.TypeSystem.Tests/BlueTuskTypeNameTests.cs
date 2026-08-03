namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTypeNameTests
{
    [Theory]
    [InlineData("app.order_status", "app", "order_status")]
    [InlineData("  app.order_status  ", "app", "order_status")]
    [InlineData("\"App Schema\".\"Order.Status\"", "App Schema", "Order.Status")]
    [InlineData("\"App\"\"Schema\".\"Order\"\"Status\"", "App\"Schema", "Order\"Status")]
    public void Parses_schema_qualified_type_names(string value, string expectedSchema, string expectedName)
    {
        var typeName = BlueTuskTypeName.Parse(value);

        Assert.Equal(expectedSchema, typeName.Schema);
        Assert.Equal(expectedName, typeName.Name);
    }

    [Theory]
    [InlineData("order_status")]
    [InlineData("app.")]
    [InlineData(".order_status")]
    [InlineData("\"app.order_status")]
    [InlineData("app.ord\"er")]
    public void Rejects_unqualified_or_malformed_type_names(string value)
    {
        Assert.Throws<FormatException>(() => BlueTuskTypeName.Parse(value));
    }
}
