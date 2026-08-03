using System.Net;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Sync.Testing;
using Xunit.Sdk;

namespace BlueTusk.Sync.OpenSearch.Tests;

public sealed class OpenSearchSyncConformanceTests
{
    [Fact]
    public async Task OpenSearch_passes_shared_destination_conformance()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_OPENSEARCH_URL");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SkipException.ForSkip("BLUETUSK_OPENSEARCH_URL is not configured.");
        }

        var endpoint = new Uri(value.EndsWith('/') ? value : value + '/', UriKind.Absolute);
        using var client = new HttpClient { BaseAddress = endpoint };
        var prefix = "bt-sync-conformance-" + Guid.NewGuid().ToString("N");
        var harness = new OpenSearchHarness(client, prefix);
        try
        {
            var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

            Assert.True(result.QuarantineVerified);
            Assert.False(result.Capabilities.HasFlag(SyncDestinationCapabilities.TransactionalBatches));
        }
        finally
        {
            await DeleteIndexesAsync(client, prefix);
        }
    }

    private static async Task DeleteIndexesAsync(HttpClient client, string prefix)
    {
        using var list = await client.GetAsync(
            $"_cat/indices/{prefix}-*?format=json&h=index",
            TestContext.Current.CancellationToken);
        if (list.StatusCode is HttpStatusCode.NotFound)
        {
            return;
        }

        list.EnsureSuccessStatusCode();
        var payload = await list.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(payload);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var index = item.GetProperty("index").GetString();
            if (index is null || !index.StartsWith(prefix + "-", StringComparison.Ordinal))
            {
                continue;
            }

            using var response = await client.DeleteAsync(
                index,
                TestContext.Current.CancellationToken);
            if (response.StatusCode is not HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }
    }

    private sealed class OpenSearchHarness(HttpClient client, string prefix)
        : ISyncDestinationConformanceHarness
    {
        private readonly OpenSearchSyncOptions _options = new()
        {
            Client = client,
            IndexPrefix = prefix,
            NumberOfReplicas = 0,
            RefreshAfterWrite = true,
            MaxDocumentBytes = 1024 * 1024,
            MaxBulkBytes = 4 * 1024 * 1024,
        };

        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } =
            new("conformance-system", "conformance-database", "conformance-slot", "public:conformance");

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(new OpenSearchSyncDestination(_options));
        }

        public async ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            var openSearch = Assert.IsType<OpenSearchSyncDestination>(destination);
            var content = await openSearch.ReadDocumentAsync(
                PipelineId,
                "conformance",
                "42",
                cancellationToken);
            Assert.NotNull(content);
            var expected = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? "{\"stage\":\"snapshot\"}"
                : "{\"stage\":\"transaction\"}";
            Assert.Equal(expected, Encoding.UTF8.GetString(content.Value.Span));
        }
    }
}
