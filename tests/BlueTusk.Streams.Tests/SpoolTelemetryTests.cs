using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace BlueTusk.Streams.Tests;

[CollectionDefinition(DisableParallelization = true)]
public sealed class SpoolTelemetryIsolation;

[Collection(typeof(SpoolTelemetryIsolation))]
public sealed class SpoolTelemetryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Completion_reports_each_attempted_operation_without_sensitive_tags(bool failRename, bool throwListener)
    {
        var measurements = new ConcurrentQueue<(double Duration, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == BlueTuskStreamsDiagnostics.InstrumentationName &&
                    instrument.Name == "bluetusk.streams.spool.operation.duration")
                {
                    Assert.Equal("s", instrument.Unit);
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            measurements.Enqueue((value, tags.ToArray()));
            if (throwListener) { throw new InvalidOperationException("Injected listener failure."); }
        });
        listener.Start();
        var directory = Directory.CreateTempSubdirectory("bluetusk-spool-metrics-").FullName;
        try
        {
            var spool = new FileTransactionSpool(new FileTransactionSpoolOptions { DirectoryPath = directory });
            await using (var writer = await spool.CreateAsync(new TransactionSpoolKey("never-emit-this-source", 123)))
            {
                await writer.AppendAsync("private-row-content"u8.ToArray());
                if (failRename)
                {
                    var partialPath = Assert.Single(Directory.EnumerateFiles(directory, "*.partial"));
                    var readyPath = Path.ChangeExtension(partialPath, ".ready");
                    Directory.CreateDirectory(readyPath);
                    var exception = await Record.ExceptionAsync(async () => await writer.CompleteAsync());
                    Assert.True(exception is IOException or UnauthorizedAccessException);
                    Directory.Delete(readyPath);
                }
                else
                {
                    await using var reader = await writer.CompleteAsync();
                    await foreach (var record in reader.ReadRecordsAsync())
                    {
                        Assert.True(record.Span.SequenceEqual("private-row-content"u8));
                    }
                }
            }
            Assert.Equal(0, spool.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            var values = measurements.ToArray();
            Assert.Equal(3, values.Length);
            Assert.Equal(["flush", "close", "rename"], values.Select(value => value.Tags[0].Value));
            foreach (var value in values)
            {
                Assert.True(double.IsFinite(value.Duration) && value.Duration >= 0);
                Assert.Equal(2, value.Tags.Length);
                Assert.Equal("bluetusk.streams.spool.operation", value.Tags[0].Key);
                Assert.Equal("bluetusk.streams.spool.outcome", value.Tags[1].Key);
                Assert.Equal(failRename && Equals(value.Tags[0].Value, "rename") ? "failure" : "success", value.Tags[1].Value);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
