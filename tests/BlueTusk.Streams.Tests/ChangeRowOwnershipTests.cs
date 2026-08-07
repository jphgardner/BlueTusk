using System.Text;

namespace BlueTusk.Streams.Tests;

public sealed class ChangeRowOwnershipTests
{
    [Fact]
    public void Public_change_table_constructor_defensively_copies_columns()
    {
        var columns = new[]
        {
            new ChangeColumn(0, "id", 23, -1, true),
            new ChangeColumn(1, "name", 25, -1, false),
        };

        var table = new ChangeTable(1, "public", "people", 'd', columns);
        columns[0] = new ChangeColumn(0, "mutated", 25, -1, false);

        Assert.Equal("id", table.Columns[0].Name);
        Assert.True(table.Columns[0].IsKey);
    }

    [Fact]
    public void Public_change_row_constructor_defensively_copies_values()
    {
        var table = new ChangeTable(
            1,
            "public",
            "people",
            'd',
            [
                new ChangeColumn(0, "id", 23, -1, true),
                new ChangeColumn(1, "name", 25, -1, false),
            ]);
        var id = ChangeColumnValue.FromValue("1"u8, ChangeValueEncoding.Text);
        var name = ChangeColumnValue.FromValue("Ada"u8, ChangeValueEncoding.Text);
        var values = new[] { id, name };

        var row = new ChangeRow(table, values);
        values[0] = ChangeColumnValue.DatabaseNull;

        Assert.Same(id, row[0]);
        Assert.Same(id, row["id"]);
        Assert.Equal("Ada", Encoding.UTF8.GetString(row["name"].Data.Span));
        Assert.Same(row, row.Values);
    }
}
