using BlueTusk.Data.Copy;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskBinaryCopyTests
{
    private static readonly byte[] BinaryRow =
        Convert.FromHexString(
            "5047434F50590AFF0D0A00" +
            "00000000" +
            "00000000" +
            "0002" +
            "000000040000002A" +
            "000000026869" +
            "FFFF");

    [Fact]
    public async Task Importer_writes_postgresql_binary_header_rows_and_trailer()
    {
        var pipe = new BlueTuskCopyPipe();
        var completion = new TaskCompletionSource<BlueTuskRawCopyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var importer = new BlueTuskBinaryImporter(
            pipe,
            completion.Task,
            BlueTuskBuiltInTypes.CreateRegistry(),
            columnCount: 2);

        await importer.InitializeAsync(CancellationToken.None);
        await importer.StartRowAsync(CancellationToken.None);
        await importer.WriteAsync(42, CancellationToken.None);
        await importer.WriteAsync("hi", CancellationToken.None);
        var completeTask = importer.CompleteAsync(CancellationToken.None).AsTask();
        completion.SetResult(BinaryResult(rows: 1, BinaryRow.Length));

        Assert.Equal(1, await completeTask);
        using var destination = new MemoryStream();
        await pipe.CopyToAsync(destination, CancellationToken.None);
        Assert.Equal(BinaryRow, destination.ToArray());
    }

    [Fact]
    public async Task Exporter_reads_fragmented_postgresql_binary_rows()
    {
        var pipe = new BlueTuskCopyPipe();
        var completion = new TaskCompletionSource<BlueTuskRawCopyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var exporter = new BlueTuskBinaryExporter(
            pipe,
            completion.Task,
            BlueTuskBuiltInTypes.CreateRegistry(),
            columnCount: 2);

        var producer = ProduceFragmentedAsync(pipe);
        completion.SetResult(BinaryResult(rows: 1, BinaryRow.Length));
        await exporter.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, await exporter.StartRowAsync(CancellationToken.None));
        Assert.Equal(42, await exporter.ReadAsync<int>(CancellationToken.None));
        Assert.Equal("hi", await exporter.ReadAsync<string>(CancellationToken.None));
        Assert.Equal(-1, await exporter.StartRowAsync(CancellationToken.None));
        await producer;
    }

    [Fact]
    public async Task Exporter_can_preserve_raw_binary_fields_without_type_decoding()
    {
        var pipe = new BlueTuskCopyPipe();
        var completion = new TaskCompletionSource<BlueTuskRawCopyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var exporter = new BlueTuskBinaryExporter(
            pipe,
            completion.Task,
            BlueTuskBuiltInTypes.CreateRegistry(),
            columnCount: 2);

        var producer = ProduceFragmentedAsync(pipe);
        completion.SetResult(BinaryResult(rows: 1, BinaryRow.Length));
        await exporter.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, await exporter.StartRowAsync(CancellationToken.None));
        var first = await exporter.ReadRawAsync();
        var second = await exporter.ReadRawAsync();
        Assert.True(first.HasValue);
        Assert.True(second.HasValue);
        Assert.Equal(Convert.FromHexString("0000002A"), first.GetValueOrDefault().ToArray());
        Assert.Equal("hi", System.Text.Encoding.UTF8.GetString(second.GetValueOrDefault().Span));
        Assert.Equal(-1, await exporter.StartRowAsync(CancellationToken.None));
        await producer;
    }

    [Fact]
    public async Task Importer_rejects_incomplete_rows()
    {
        var pipe = new BlueTuskCopyPipe();
        var completion = new TaskCompletionSource<BlueTuskRawCopyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var importer = new BlueTuskBinaryImporter(
            pipe,
            completion.Task,
            BlueTuskBuiltInTypes.CreateRegistry(),
            columnCount: 2);

        await importer.InitializeAsync(CancellationToken.None);
        await importer.StartRowAsync(CancellationToken.None);
        await importer.WriteAsync(42, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.CompleteAsync(CancellationToken.None).AsTask());
        completion.SetException(new IOException("Importer intentionally aborted."));
    }

    private static BlueTuskRawCopyResult BinaryResult(long rows, long bytes) =>
        new(
            BlueTuskCopyDataFormat.Binary,
            [BlueTuskCopyDataFormat.Binary, BlueTuskCopyDataFormat.Binary],
            rows,
            bytes);

    private static async Task ProduceFragmentedAsync(BlueTuskCopyPipe pipe)
    {
        foreach (var value in BinaryRow)
        {
            await pipe.WriteChunkAsync(new byte[] { value }, CancellationToken.None);
        }

        pipe.CompleteWriting();
    }
}
