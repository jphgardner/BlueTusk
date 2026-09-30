using BlueTusk.Data;

namespace BlueTusk.Studio.Tests;

public sealed class StudioAdmissionTests
{
    [Fact]
    public async Task Replicas_share_global_and_scope_caps_and_release_rejected_scope_slots()
    {
        await using var firstDataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        await using var secondDataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var domain = "studio_gate_" + Guid.NewGuid().ToString("N");
        var first = new PostgreSqlStudioAdmission(firstDataSource, domain, 2, 1);
        var second = new PostgreSqlStudioAdmission(secondDataSource, domain, 2, 1);

        await using var tenantA = Assert.IsAssignableFrom<IAsyncDisposable>(
            await first.TryAcquireAsync("tenant-a", TestContext.Current.CancellationToken));
        Assert.Null(await second.TryAcquireAsync("tenant-a", TestContext.Current.CancellationToken));
        await using var tenantB = Assert.IsAssignableFrom<IAsyncDisposable>(
            await second.TryAcquireAsync("tenant-b", TestContext.Current.CancellationToken));
        Assert.Null(await first.TryAcquireAsync("tenant-c", TestContext.Current.CancellationToken));

        await tenantA.DisposeAsync();
        await using var tenantC = Assert.IsAssignableFrom<IAsyncDisposable>(
            await second.TryAcquireAsync("tenant-c", TestContext.Current.CancellationToken));
        Assert.Null(await first.TryAcquireAsync("tenant-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admission_rejects_missing_scope_before_opening_a_connection()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var gate = new PostgreSqlStudioAdmission(dataSource, "studio_invalid_scope", 2, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => gate.TryAcquireAsync("legacy-unknown", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => gate.TryAcquireAsync("", TestContext.Current.CancellationToken).AsTask());
    }
}
