using BlueTusk.Streams.Storage.File;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Tests;

public sealed class StateStoreConformanceTests
{
    [Fact]
    public async Task Memory_store_passes_the_public_conformance_kit()
    {
        var report = await ChangeStreamStateStoreConformance.RunAsync(
            new MemoryChangeStreamStateStore(),
            "memory");

        Assert.Equal("memory", report.StoreName);
        Assert.True(report.Assertions >= 10);
    }

    [Fact]
    public async Task File_store_passes_the_public_conformance_kit()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var report = await ChangeStreamStateStoreConformance.RunAsync(
                CreateStore(directory),
                "file");

            Assert.Equal("file", report.StoreName);
            Assert.True(report.Assertions >= 10);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task File_store_round_trips_state_across_instances()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = SourceIdentity();
            var key = ChangeStreamStateKey.Create(source, "orders");
            var first = CreateStore(directory);
            var lease = Assert.IsType<ChangeStreamLease>(
                (await first.AcquireAsync(key, "worker-1", TimeSpan.FromMinutes(1))).Lease);
            var expected = ChangeStreamCheckpoint.CreateInitial(
                    source,
                    "database-system-id",
                    "pgoutput",
                    "mapping-v1")
                .MoveTo(new BlueTuskLogSequenceNumber(100), 0);
            var write = await first.CompareExchangeAsync(key, -1, expected, lease);
            Assert.Equal(ChangeCheckpointWriteStatus.Stored, write.Status);

            var reopened = CreateStore(directory);

            Assert.Equal(expected, await reopened.ReadAsync(key));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task File_store_serializes_competing_process_owners()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var key = ChangeStreamStateKey.Create(SourceIdentity(), "orders");
            var first = CreateStore(directory);
            var second = CreateStore(directory);

            var attempts = await Task.WhenAll(
                first.AcquireAsync(key, "worker-1", TimeSpan.FromMinutes(1)).AsTask(),
                second.AcquireAsync(key, "worker-2", TimeSpan.FromMinutes(1)).AsTask());

            Assert.Single(attempts, result => result.Status == ChangeLeaseAcquireStatus.Acquired);
            Assert.Single(
                attempts,
                result => result.Status == ChangeLeaseAcquireStatus.HeldByAnotherOwner);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task File_store_rejects_checksum_tampering()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var key = ChangeStreamStateKey.Create(SourceIdentity(), "orders");
            var store = CreateStore(directory);
            _ = await store.AcquireAsync(key, "worker-1", TimeSpan.FromMinutes(1));
            var statePath = Assert.Single(Directory.GetFiles(directory, "*.state"));
            var bytes = await System.IO.File.ReadAllBytesAsync(statePath);
            bytes[^1] ^= 0xFF;
            await System.IO.File.WriteAllBytesAsync(statePath, bytes);

            await Assert.ThrowsAsync<FileChangeStreamStateStoreException>(
                () => store.ReadAsync(key).AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FileChangeStreamStateStore CreateStore(string directory) =>
        new(new FileChangeStreamStateStoreOptions { DirectoryPath = directory });

    private static ChangeSourceIdentity SourceIdentity() =>
        new("739463", "app", "orders_slot", "public:orders");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "bluetusk-streams-state-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
