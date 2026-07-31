using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSynchronousNativeIntegrationTests
{
    [Fact]
    public void Sync_text_and_binary_copy_stream_and_leave_the_connection_reusable()
    {
        using var connection = OpenConnection();
        using (var setup = new BlueTuskCommand(
            "CREATE TEMP TABLE bluetusk_sync_copy (id int4 NOT NULL, value text NOT NULL)",
            connection))
        {
            _ = setup.ExecuteNonQuery();
        }

        var imported = connection.CopyTextFrom(
            "COPY bluetusk_sync_copy (id, value) FROM STDIN WITH (FORMAT csv)",
            new StringReader("1,one\n2,two\n"));
        Assert.Equal(2, imported.RowsAffected);

        var exportedText = new StringWriter();
        var exported = connection.CopyTextTo(
            "COPY (SELECT id, value FROM bluetusk_sync_copy ORDER BY id) TO STDOUT WITH (FORMAT csv)",
            exportedText);
        Assert.Equal(2, exported.RowsAffected);
        Assert.Equal("1,one\n2,two\n", exportedText.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));

        using (var importer = connection.BeginBinaryImport(
            "COPY bluetusk_sync_copy (id, value) FROM STDIN BINARY"))
        {
            importer.StartRow();
            importer.Write(3);
            importer.Write("three");
            Assert.Equal(1, importer.Complete());
        }

        using (var exporter = connection.BeginBinaryExport(
            "COPY (SELECT id, value FROM bluetusk_sync_copy ORDER BY id) TO STDOUT BINARY"))
        {
            for (var expected = 1; expected <= 3; expected++)
            {
                Assert.Equal(2, exporter.StartRow());
                Assert.Equal(expected, exporter.Read<int>());
                Assert.Equal(expected switch { 1 => "one", 2 => "two", _ => "three" }, exporter.Read<string>());
            }

            Assert.Equal(-1, exporter.StartRow());
        }

        using (connection.BeginBinaryImport(
            "COPY bluetusk_sync_copy (id, value) FROM STDIN BINARY"))
        {
            // Disposing before Complete sends CopyFail and drains ReadyForQuery.
        }

        using var valid = new BlueTuskCommand("SELECT count(*) FROM bluetusk_sync_copy", connection);
        Assert.Equal(3L, valid.ExecuteScalar());
    }

    [Fact]
    public void Sync_listen_wait_and_unlisten_deliver_notifications()
    {
        var channel = $"bluetusk_sync_{Guid.NewGuid():N}";
        using var listener = OpenConnection();
        using var publisher = OpenConnection();
        listener.Listen(channel);

        using (var notify = new BlueTuskCommand(
            $"NOTIFY {BlueTuskSql.QuoteIdentifier(channel)}, 'sync payload'",
            publisher))
        {
            _ = notify.ExecuteNonQuery();
        }

        var notification = listener.WaitForNotification();
        Assert.Equal(channel, notification.Channel);
        Assert.Equal("sync payload", notification.Payload);
        listener.Unlisten(channel);
    }

    [Fact]
    public void Sync_large_object_stream_supports_read_write_seek_truncate_and_commit()
    {
        using var connection = OpenConnection();
        var objectId = connection.CreateLargeObject();
        try
        {
            var payload = Encoding.UTF8.GetBytes("synchronous large object");
            using (var stream = connection.OpenLargeObject(objectId, FileAccess.ReadWrite))
            {
                stream.Write(payload, 0, payload.Length);
                Assert.Equal(payload.Length, stream.Length);
                Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
                var read = new byte[payload.Length];
                Assert.Equal(read.Length, stream.Read(read, 0, read.Length));
                Assert.Equal(payload, read);
                stream.SetLength(11);
                Assert.Equal(11, stream.Length);
            }

            using var reopened = connection.OpenLargeObject(objectId, FileAccess.Read);
            var truncated = new byte[11];
            Assert.Equal(truncated.Length, reopened.Read(truncated, 0, truncated.Length));
            Assert.Equal("synchronous", Encoding.UTF8.GetString(truncated));
        }
        finally
        {
            connection.DeleteLargeObject(objectId);
        }
    }

    private static BlueTuskConnection OpenConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        var connection = new BlueTuskConnection(settings.ConnectionString);
        connection.Open();
        return connection;
    }
}
