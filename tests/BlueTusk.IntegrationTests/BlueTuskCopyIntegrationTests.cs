using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Data.Copy;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskCopyIntegrationTests
{
    private static readonly int[] SampleScores = [1, 2, 3];

    [Fact]
    public async Task Raw_copy_streams_support_csv_text_and_binary_round_trips()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_copy_values (id int4, name text, note text)");

        const string csv = "1,Alice,hello\n2,\"Bob, Jr.\",goodbye\n";
        await using (var source = new MemoryStream(Encoding.UTF8.GetBytes(csv)))
        {
            var imported = await connection.CopyFromAsync(
                "COPY bluetusk_copy_values (id, name, note) FROM STDIN WITH (FORMAT CSV)",
                source,
                CancellationToken.None);

            Assert.Equal(BlueTuskCopyDataFormat.Text, imported.Format);
            Assert.All(
                imported.ColumnFormats,
                format => Assert.Equal(BlueTuskCopyDataFormat.Text, format));
            Assert.Equal(2, imported.RowsAffected);
            Assert.Equal(source.Length, imported.BytesTransferred);
        }

        await using (var textDestination = new MemoryStream())
        {
            var exported = await connection.CopyToAsync(
                """
                COPY (
                    SELECT id, name, note
                    FROM bluetusk_copy_values
                    ORDER BY id
                ) TO STDOUT WITH (FORMAT TEXT)
                """,
                textDestination,
                CancellationToken.None);

            Assert.Equal(BlueTuskCopyDataFormat.Text, exported.Format);
            Assert.Equal(2, exported.RowsAffected);
            Assert.Equal(
                "1\tAlice\thello\n2\tBob, Jr.\tgoodbye\n",
                Encoding.UTF8.GetString(textDestination.ToArray()));
        }

        byte[] binary;
        await using (var binaryDestination = new MemoryStream())
        {
            var exported = await connection.CopyToAsync(
                """
                COPY (
                    SELECT id, name, note
                    FROM bluetusk_copy_values
                    ORDER BY id
                ) TO STDOUT WITH (FORMAT BINARY)
                """,
                binaryDestination,
                CancellationToken.None);

            binary = binaryDestination.ToArray();
            Assert.Equal(BlueTuskCopyDataFormat.Binary, exported.Format);
            Assert.Equal(2, exported.RowsAffected);
            Assert.StartsWith("PGCOPY\n\u00ff\r\n\0", Encoding.Latin1.GetString(binary));
        }

        await ExecuteAsync(connection, "TRUNCATE bluetusk_copy_values");
        await using (var binarySource = new MemoryStream(binary))
        {
            var imported = await connection.CopyFromAsync(
                "COPY bluetusk_copy_values (id, name, note) FROM STDIN WITH (FORMAT BINARY)",
                binarySource,
                CancellationToken.None);

            Assert.Equal(BlueTuskCopyDataFormat.Binary, imported.Format);
            Assert.Equal(2, imported.RowsAffected);
        }

        await using var count = new BlueTuskCommand(
            "SELECT count(*)::int8 FROM bluetusk_copy_values",
            connection);
        Assert.Equal(2, await count.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task Text_copy_helpers_stream_unicode_csv_without_owning_text_objects()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_text_copy (id int4, name text, note text)");
        using var source = new StringReader("1,\"Chloé 🐘\",\"line one\"\n");

        var imported = await connection.CopyTextFromAsync(
            "COPY bluetusk_text_copy FROM STDIN WITH (FORMAT CSV)",
            source,
            CancellationToken.None);

        Assert.Equal(1, imported.RowsAffected);
        using var destination = new StringWriter(
            System.Globalization.CultureInfo.InvariantCulture);
        var exported = await connection.CopyTextToAsync(
            "COPY bluetusk_text_copy TO STDOUT WITH (FORMAT CSV)",
            destination,
            CancellationToken.None);

        Assert.Equal(1, exported.RowsAffected);
        Assert.Equal("1,Chloé 🐘,line one\n", destination.ToString());
        Assert.Equal(-1, source.Read());
        destination.Write("still open");
    }

    [Fact]
    public async Task Typed_binary_copy_imports_and_exports_rows()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            connection,
            """
            CREATE TEMP TABLE bluetusk_binary_copy (
                id int4,
                name text,
                active bool,
                happened_at timestamptz,
                token uuid,
                note text,
                scores int4[]
            )
            """);
        var firstTime = new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.FromHours(2));
        var secondTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var firstToken = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondToken = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

        await using (var importer = await connection.BeginBinaryImportAsync(
                         "COPY bluetusk_binary_copy FROM STDIN WITH (FORMAT BINARY)",
                         CancellationToken.None))
        {
            await importer.StartRowAsync(CancellationToken.None);
            await importer.WriteAsync(1, CancellationToken.None);
            await importer.WriteAsync("Chloé 🐘", CancellationToken.None);
            await importer.WriteAsync(true, CancellationToken.None);
            await importer.WriteAsync(firstTime, CancellationToken.None);
            await importer.WriteAsync(firstToken, CancellationToken.None);
            await importer.WriteAsync<string>(null, CancellationToken.None);
            await importer.WriteAsync(SampleScores, CancellationToken.None);

            await importer.StartRowAsync(CancellationToken.None);
            await importer.WriteAsync(2, CancellationToken.None);
            await importer.WriteAsync("BlueTusk", CancellationToken.None);
            await importer.WriteAsync(false, CancellationToken.None);
            await importer.WriteAsync(secondTime, CancellationToken.None);
            await importer.WriteAsync(secondToken, CancellationToken.None);
            await importer.WriteAsync("complete", CancellationToken.None);
            await importer.WriteAsync(Array.Empty<int>(), CancellationToken.None);

            Assert.Equal(2, await importer.CompleteAsync(CancellationToken.None));
        }

        await using var exporter = await connection.BeginBinaryExportAsync(
            """
            COPY (
                SELECT id, name, active, happened_at, token, note, scores
                FROM bluetusk_binary_copy
                ORDER BY id
            ) TO STDOUT WITH (FORMAT BINARY)
            """,
            CancellationToken.None);

        Assert.Equal(7, await exporter.StartRowAsync(CancellationToken.None));
        Assert.Equal(1, await exporter.ReadAsync<int>(CancellationToken.None));
        Assert.Equal("Chloé 🐘", await exporter.ReadAsync<string>(CancellationToken.None));
        Assert.True(await exporter.ReadAsync<bool>(CancellationToken.None));
        Assert.Equal(
            firstTime.UtcDateTime,
            (await exporter.ReadAsync<DateTimeOffset>(CancellationToken.None)).UtcDateTime);
        Assert.Equal(firstToken, await exporter.ReadAsync<Guid>(CancellationToken.None));
        Assert.Null(await exporter.ReadAsync<string>(CancellationToken.None));
        Assert.Equal(
            SampleScores,
            await exporter.ReadAsync<int[]>(CancellationToken.None));

        Assert.Equal(7, await exporter.StartRowAsync(CancellationToken.None));
        Assert.Equal(2, await exporter.ReadAsync<int>(CancellationToken.None));
        Assert.Equal("BlueTusk", await exporter.ReadAsync<string>(CancellationToken.None));
        Assert.False(await exporter.ReadAsync<bool>(CancellationToken.None));
        Assert.Equal(
            secondTime,
            await exporter.ReadAsync<DateTimeOffset>(CancellationToken.None));
        Assert.Equal(secondToken, await exporter.ReadAsync<Guid>(CancellationToken.None));
        Assert.Equal("complete", await exporter.ReadAsync<string>(CancellationToken.None));
        Assert.Empty((await exporter.ReadAsync<int[]>(CancellationToken.None))!);
        Assert.Equal(-1, await exporter.StartRowAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Empty_binary_exports_initialize_when_transfer_completes_immediately()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        for (var iteration = 0; iteration < 32; iteration++)
        {
            await using var exporter = await connection.BeginBinaryExportAsync(
                "COPY (SELECT 1::int4 WHERE false) TO STDOUT WITH (FORMAT BINARY)",
                CancellationToken.None);
            Assert.Equal(-1, await exporter.StartRowAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Binary_copy_disposal_and_format_mismatch_abort_cleanly()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_aborted_binary_copy (id int4, name text)");

        var importer = await connection.BeginBinaryImportAsync(
            "COPY bluetusk_aborted_binary_copy FROM STDIN WITH (FORMAT BINARY)",
            CancellationToken.None);
        await importer.StartRowAsync(CancellationToken.None);
        await importer.WriteAsync(1, CancellationToken.None);
        await importer.DisposeAsync();

        var exporter = await connection.BeginBinaryExportAsync(
            """
            COPY (
                SELECT value, value::text
                FROM generate_series(1, 10000) AS value
            ) TO STDOUT WITH (FORMAT BINARY)
            """,
            CancellationToken.None);
        Assert.Equal(2, await exporter.StartRowAsync(CancellationToken.None));
        await exporter.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.BeginBinaryImportAsync(
                "COPY bluetusk_aborted_binary_copy FROM STDIN WITH (FORMAT CSV)",
                CancellationToken.None).AsTask());

        await using var verify = new BlueTuskCommand("SELECT $1::int4", connection);
        verify.Parameters.Add(new BlueTuskParameter<int>(42));
        Assert.Equal(42, await verify.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_copy_streams_abort_and_leave_the_connection_reusable()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_failed_copy (id int4, value text)");

        await using (var source = new ThrowingReadStream("1\tpartial\n"u8.ToArray()))
        {
            await Assert.ThrowsAsync<IOException>(
                () => connection.CopyFromAsync(
                    "COPY bluetusk_failed_copy FROM STDIN",
                    source,
                    CancellationToken.None).AsTask());
        }

        await using (var destination = new ThrowingWriteStream())
        {
            await Assert.ThrowsAsync<IOException>(
                () => connection.CopyToAsync(
                    "COPY (SELECT generate_series(1, 1000)) TO STDOUT",
                    destination,
                    CancellationToken.None).AsTask());
        }

        await using var verify = new BlueTuskCommand("SELECT $1::int4", connection);
        verify.Parameters.Add(new BlueTuskParameter<int>(42));
        Assert.Equal(42, await verify.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_copy_from_aborts_and_leaves_the_connection_reusable()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_cancelled_copy (id int4)");
        await using var source = new CancellableReadStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.CopyFromAsync(
                "COPY bluetusk_cancelled_copy FROM STDIN",
                source,
                cancellation.Token).AsTask());

        await using var verify = new BlueTuskCommand("SELECT $1::int4", connection);
        verify.Parameters.Add(new BlueTuskParameter<int>(42));
        Assert.Equal(42, await verify.ExecuteScalarAsync<int>(CancellationToken.None));
    }

    private static async ValueTask ExecuteAsync(
        BlueTuskConnection connection,
        string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }

    private sealed class ThrowingReadStream(byte[] bytes) : Stream
    {
        private int _offset;
        private bool _returnedData;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_returnedData)
            {
                throw new IOException("Simulated COPY source failure.");
            }

            _returnedData = true;
            var count = Math.Min(buffer.Length, bytes.Length);
            bytes.AsSpan(0, count).CopyTo(buffer.Span);
            _offset = count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Simulated COPY destination failure."));

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CancellableReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
