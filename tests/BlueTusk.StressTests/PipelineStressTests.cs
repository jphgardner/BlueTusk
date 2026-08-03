using System.Globalization;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.StressTests;

public sealed class PipelineStressTests
{
    [Fact]
    public async Task Concurrent_pipeline_groups_preserve_results_errors_and_session_reuse()
    {
        var workers = 8 * StressScale;
        const int iterations = 15;

        await Task.WhenAll(
            Enumerable.Range(0, workers).Select(
                worker => Task.Run(
                    async () =>
                    {
                        await using var session = await BlueTuskSession.OpenAsync(CreateOptions());
                        Assert.True(session.Capabilities.SupportsPipelineMode);

                        for (var iteration = 0; iteration < iterations; iteration++)
                        {
                            var first = worker * iterations + iteration;
                            var result = await session.ExecutePipelineAsync(
                            [
                                new BlueTuskPipelineGroup(
                                [
                                    TextQuery(FormattableString.Invariant($"SELECT {first}::int4")),
                                    TextQuery(FormattableString.Invariant($"SELECT {first + 1}::int4")),
                                ]),
                                new BlueTuskPipelineGroup(
                                [
                                    TextQuery("SELECT 1::int4 / 0::int4"),
                                ]),
                                new BlueTuskPipelineGroup(
                                [
                                    TextQuery(FormattableString.Invariant($"SELECT {first + 2}::int4")),
                                ]),
                            ]);

                            Assert.Equal(
                                first.ToString(CultureInfo.InvariantCulture),
                                ReadSingleText(result.Groups[0].Result.ResultSets[0]));
                            Assert.Equal(
                                (first + 1).ToString(CultureInfo.InvariantCulture),
                                ReadSingleText(result.Groups[0].Result.ResultSets[1]));
                            Assert.Equal("22012", result.Groups[1].Error!.SqlState);
                            Assert.Equal(
                                (first + 2).ToString(CultureInfo.InvariantCulture),
                                ReadSingleText(Assert.Single(result.Groups[2].Result.ResultSets)));
                        }

                        var reused = await session.ExecuteSimpleQueryAsync("SELECT 42::int4");
                        Assert.Equal("42", ReadSingleText(Assert.Single(reused.ResultSets)));
                    })));
    }

    [Fact]
    public async Task Repeated_pipeline_cancellation_drains_all_sent_groups_before_reuse()
    {
        var workers = 4 * StressScale;
        const int iterations = 3;

        await Task.WhenAll(
            Enumerable.Range(0, workers).Select(
                _ => Task.Run(
                    async () =>
                    {
                        await using var session = await BlueTuskSession.OpenAsync(CreateOptions());
                        for (var iteration = 0; iteration < iterations; iteration++)
                        {
                            using var cancellation = new CancellationTokenSource(
                                TimeSpan.FromMilliseconds(150));
                            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                                () => session.ExecutePipelineAsync(
                                [
                                    new BlueTuskPipelineGroup(
                                    [
                                        TextQuery("SELECT pg_sleep(5)"),
                                    ]),
                                    new BlueTuskPipelineGroup(
                                    [
                                        TextQuery("SELECT 41::int4"),
                                    ]),
                                ], cancellation.Token).AsTask());

                            var reused = await session.ExecuteSimpleQueryAsync("SELECT 42::int4");
                            Assert.Equal("42", ReadSingleText(Assert.Single(reused.ResultSets)));
                        }
                    })));
    }

    private static BlueTuskBatchQuery TextQuery(string sql) =>
        new(sql, [], UseBinaryResults: false);

    private static string ReadSingleText(BlueTuskResultSet resultSet) =>
        Encoding.UTF8.GetString(Assert.Single(resultSet.Rows).Values[0]!.Value.Span);

    private static int StressScale
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BLUETUSK_STRESS_SCALE");
            return int.TryParse(raw, out var scale) && scale > 0 ? scale : 1;
        }
    }

    private static BlueTuskClientOptions CreateOptions()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        return new BlueTuskClientOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
    }
}
