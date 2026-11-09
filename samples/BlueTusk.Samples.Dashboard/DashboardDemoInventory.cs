using System.Globalization;
using BlueTusk.ControlPlane;

internal static class DashboardDemoInventory
{
    internal static async Task<IControlPlaneFleetQueryService> CreateAsync(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 10_000);
        var store = new InMemoryManagedDeploymentStore();
        string[] tenants = ["acme", "northwind", "contoso"];
        string[] products = ["orders", "inventory", "finance", "sales"];
        ManagedWorkloadKind[] kinds = [ManagedWorkloadKind.Streams, ManagedWorkloadKind.Sync, ManagedWorkloadKind.Live];
        for (var index = 0; index < count; index++)
        {
            var state = index % 13 == 0 ? ManagedDeploymentState.Degraded
                : index % 11 == 0 ? ManagedDeploymentState.Paused : ManagedDeploymentState.Ready;
            var spec = new ManagedDeploymentSpec(
                "erp-" + products[index % products.Length] + "-" + (index + 1).ToString("D5", CultureInfo.InvariantCulture),
                tenants[index % tenants.Length], "kubernetes", index % 2 == 0 ? "uk-south" : "eu-west", 1,
                state == ManagedDeploymentState.Paused, true,
                [new(kinds[index % kinds.Length], "1.2.0", new(2, 500, 512L * 1024 * 1024, 10L * 1024 * 1024 * 1024),
                    [], new Dictionary<string, string>())], new Dictionary<string, string>());
            var deployment = await store.PutAsync(spec, 0);
            var lease = await store.TryAcquireAsync(spec.DeploymentId, "local-demo", TimeSpan.FromMinutes(5)) ??
                throw new InvalidOperationException("The local demonstration lease could not be acquired.");
            await store.UpdateStatusAsync(spec.DeploymentId, deployment.Status with
            {
                State = state,
                Revision = 1,
                ObservedGeneration = 1,
                FencingToken = lease.FencingToken,
                DiagnosticCode = state == ManagedDeploymentState.Degraded ? "demo-capacity-pending" : null,
            }, 0);
            await store.ReleaseAsync(lease);
        }
        return new ManagedDeploymentFleetQueryService(store);
    }
}
