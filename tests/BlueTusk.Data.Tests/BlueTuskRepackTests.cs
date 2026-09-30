using BlueTusk.Data.Maintenance;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskRepackTests
{
    [Fact]
    public void Builds_every_native_repack_form_with_quoted_identifiers()
    {
        Assert.Equal(
            "REPACK",
            BlueTuskRepackExtensions.BuildCommandText(BlueTuskRepackRequest.ForDatabase()));
        Assert.Equal(
            "REPACK (VERBOSE) USING INDEX",
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForDatabase() with
                {
                    Verbose = true,
                    UseIndex = true,
                }));
        Assert.Equal(
            "REPACK \"events\"",
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForTable("events")));
        Assert.Equal(
            "REPACK (CONCURRENTLY) \"app\".\"events\" USING INDEX",
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForTable("events", "app") with
                {
                    Concurrently = true,
                    UseIndex = true,
                }));
        Assert.Equal(
            "REPACK (VERBOSE, ANALYZE, CONCURRENTLY) \"app\".\"event\"\"log\" " +
            "(\"tenant_id\", \"created_at\") USING INDEX \"events_cluster\"",
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForTable("event\"log", "app") with
                {
                    Verbose = true,
                    Analyze = true,
                    Concurrently = true,
                    AnalyzeColumns = ["tenant_id", "created_at"],
                    IndexName = "events_cluster",
                    CommandTimeoutSeconds = 600,
                }));
    }

    [Fact]
    public void Rejects_invalid_or_unsafe_repack_shapes_before_sending_sql()
    {
        Assert.Throws<ArgumentException>(() =>
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForDatabase() with { Concurrently = true }));
        Assert.Throws<ArgumentException>(() =>
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForDatabase() with { Analyze = true }));
        Assert.Throws<ArgumentException>(() =>
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForDatabase() with { SchemaName = "app" }));
        Assert.Throws<ArgumentException>(() =>
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForTable("events") with
                {
                    AnalyzeColumns = ["created_at"],
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForTable("events") with
                {
                    CommandTimeoutSeconds = -1,
                }));
        Assert.Throws<ArgumentException>(() =>
            BlueTuskRepackExtensions.BuildCommandText(
                BlueTuskRepackRequest.ForTable("events\0archive")));
    }

    [Fact]
    public void Reports_heap_scan_percentage_only_when_a_total_exists()
    {
        var progress = new BlueTuskRepackProgress(
            42,
            1,
            "app",
            2,
            "REPACK",
            "seq scanning heap",
            0,
            500,
            400,
            0,
            0,
            200,
            50,
            0);

        Assert.Equal(25d, progress.HeapScanPercent);
        Assert.Null((progress with { HeapBlocksTotal = 0 }).HeapScanPercent);
        Assert.Equal(100d, (progress with { HeapBlocksScanned = 300 }).HeapScanPercent);
    }
}
