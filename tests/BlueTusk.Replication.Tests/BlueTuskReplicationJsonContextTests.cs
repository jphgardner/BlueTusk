using System.Text.Json;

namespace BlueTusk.Replication.Tests;

public sealed class BlueTuskReplicationJsonContextTests
{
    [Fact]
    public void Generated_publication_metadata_preserves_quoted_unicode_and_empty_column_lists()
    {
        var columns = JsonSerializer.Deserialize("[\"id\",\"quoted\\\"column\",\"Δ\"]",
            BlueTuskReplicationJsonContext.Default.StringArray);
        Assert.NotNull(columns);
        Assert.Equal(["id", "quoted\"column", "Δ"], columns);
        Assert.Empty(JsonSerializer.Deserialize("[]", BlueTuskReplicationJsonContext.Default.StringArray)!);
        Assert.Null(JsonSerializer.Deserialize("null", BlueTuskReplicationJsonContext.Default.StringArray));
    }
}
