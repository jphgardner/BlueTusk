using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.Sync.Testing;
using Xunit.Sdk;

namespace BlueTusk.Sync.Tests;

public sealed class PostgreSqlSyncConformanceTests
{
    [Fact]
    public async Task PostgreSql_passes_shared_destination_conformance()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        await using var harness = new PostgreSqlHarness(connectionString);

        var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

        Assert.True(result.QuarantineVerified);
        Assert.True(result.Capabilities.HasFlag(SyncDestinationCapabilities.CoLocatedCheckpoint));
    }

    private sealed class PostgreSqlHarness : ISyncDestinationConformanceHarness, IAsyncDisposable
    {
        private readonly DbDataSource _dataSource;
        private readonly string _schema = "bluetusk_sync_conformance_" + Guid.NewGuid().ToString("N");

        public PostgreSqlHarness(string connectionString) =>
            _dataSource = BlueTuskDataSource.Create(connectionString);

        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } =
            new("conformance-system", "conformance-database", "conformance-slot", "public:conformance");

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(new PostgreSqlSyncDestination(new()
            {
                DestinationDataSource = _dataSource,
                ControlSchema = _schema,
                MaxDocumentBytes = 1024 * 1024,
                MaxTransactionBytes = 4 * 1024 * 1024,
            }));
        }

        public async ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT content FROM \"{_schema}\".documents WHERE pipeline_id = 'conformance' AND collection_name = 'conformance' AND document_key = '42'";
            var value = await command.ExecuteScalarAsync(cancellationToken);
            var bytes = Assert.IsType<byte[]>(value);
            Assert.Equal(Expected(stage), Encoding.UTF8.GetString(bytes));
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
            await _dataSource.DisposeAsync();
        }

        private static string Expected(SyncDestinationConformanceStage stage) =>
            stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? "{\"stage\":\"snapshot\"}"
                : "{\"stage\":\"transaction\"}";
    }
}
