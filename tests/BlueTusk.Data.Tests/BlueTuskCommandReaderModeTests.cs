using System.Data;
using BlueTusk.Client;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandReaderModeTests
{
    [Fact]
    public void Streaming_probe_tracks_command_text_and_connection_buffering()
    {
        using var connection = new BlueTuskConnection();
        using var command = new BlueTuskCommand("SELECT 1", connection)
        {
            ExecutionMode = BlueTuskCommandExecutionMode.Extended,
        };

        Assert.True(command.WillStreamReader(CommandBehavior.Default));
        command.CommandText = "SELECT 1; SELECT 2";
        Assert.False(command.WillStreamReader(CommandBehavior.Default));
        command.CommandText = "SELECT * FROM (SELECT 1) AS bounded LIMIT 2";
        Assert.True(command.WillStreamReader(CommandBehavior.Default));

        connection.UseBufferedDataReaders();
        Assert.False(command.WillStreamReader(CommandBehavior.Default));
    }
}
