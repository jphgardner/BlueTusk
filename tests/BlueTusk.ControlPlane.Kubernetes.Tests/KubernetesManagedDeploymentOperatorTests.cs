using System.Net;
using System.Text;
namespace BlueTusk.ControlPlane.Kubernetes.Tests;

public sealed class KubernetesManagedDeploymentOperatorTests
{
    private static readonly ManagedTenantQuota Quota =
        new(100, 1_000, 10_000_000, 1L << 50, 1L << 50);

    [Fact]
    public async Task Reconcile_adds_finalizer_converges_and_publishes_redacted_status()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        var client = new RecordingClient(Resource(generation: 7));
        var sut = Operator(store, provider, client);

        var first = await sut.ReconcileAsync(client.Resource, TestContext.Current.CancellationToken);
        var second = await sut.ReconcileAsync(client.Resource, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(first.Changed);
        Assert.True(second.Succeeded);
        Assert.False(second.Changed);
        Assert.Equal(1, client.FinalizerWrites);
        Assert.Contains(KubernetesManagedDeploymentOperator.Finalizer, client.Resource.Finalizers);
        Assert.NotNull(client.Status);
        Assert.Equal(ManagedDeploymentState.Ready, client.Status.State);
        Assert.Equal(7, client.Status.ObservedResourceGeneration);
        Assert.Equal(1, client.Status.ManagedGeneration);
        Assert.Equal(1, provider.ApplyCount);
        var stored = await store.GetAsync("production/orders");
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Spec.Generation);
        Assert.Equal("production/orders", stored.Spec.DeploymentId);
    }

    [Fact]
    public async Task Kubernetes_generation_jumps_advance_managed_generation_only_for_a_new_fingerprint()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        var client = new RecordingClient(Resource(generation: 1));
        var sut = Operator(store, provider, client);
        _ = await sut.ReconcileAsync(client.Resource, TestContext.Current.CancellationToken);
        client.Resource = Resource(
            generation: 19,
            finalizers: [KubernetesManagedDeploymentOperator.Finalizer],
            version: "1.2.1");

        var result = await sut.ReconcileAsync(client.Resource, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        var stored = await store.GetAsync("production/orders");
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Spec.Generation);
        Assert.Equal("1.2.1", stored.Spec.Workloads[0].Version);
        Assert.Equal(19, client.Status?.ObservedResourceGeneration);
        Assert.Equal(2, client.Status?.ManagedGeneration);
    }

    [Fact]
    public async Task Delete_protection_keeps_the_finalizer_and_reports_a_stable_failure()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        var client = new RecordingClient(Resource(
            generation: 1,
            finalizers: [KubernetesManagedDeploymentOperator.Finalizer],
            deleteProtection: true));
        var sut = Operator(store, provider, client);
        _ = await sut.ReconcileAsync(client.Resource, TestContext.Current.CancellationToken);
        client.Resource = Resource(
            generation: 1,
            finalizers: [KubernetesManagedDeploymentOperator.Finalizer],
            deleteProtection: true,
            deleting: true);

        var result = await sut.ReconcileAsync(client.Resource, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("delete-protection-enabled", result.DiagnosticCode);
        Assert.Contains(KubernetesManagedDeploymentOperator.Finalizer, client.Resource.Finalizers);
        Assert.Equal(0, provider.DeleteCount);
        Assert.Equal(ManagedDeploymentState.Failed, client.Status?.State);
    }

    [Fact]
    public async Task Http_client_pages_resources_and_uses_resource_version_json_patch_tests()
    {
        var handler = new RecordingHandler(
            ListJsonResponse(ResourceJson(finalizers: [])),
            ResourceJsonResponse(ResourceJson(finalizers: [KubernetesManagedDeploymentOperator.Finalizer])),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.default.svc"),
        };
        var client = new KubernetesApiManagedDeploymentClient(http, "production");

        var page = await client.ListAsync(100, cancellationToken: TestContext.Current.CancellationToken);
        var resource = Assert.Single(page.Resources);
        resource = await client.ReplaceFinalizersAsync(
            resource,
            [KubernetesManagedDeploymentOperator.Finalizer],
            TestContext.Current.CancellationToken);
        await client.ReplaceStatusAsync(
            resource,
            new KubernetesManagedDeploymentStatus(
                7,
                1,
                ManagedDeploymentState.Ready,
                null,
                new DateTimeOffset(2026, 8, 29, 16, 0, 0, TimeSpan.Zero)),
            TestContext.Current.CancellationToken);

        Assert.Equal("production/orders", resource.DeploymentId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("limit=100", handler.Requests[0].Path, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"/metadata/resourceVersion\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"41\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.EndsWith("/status", handler.Requests[2].Path, StringComparison.Ordinal);
        Assert.Contains("\"observedGeneration\":7", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_pass_reports_each_invalid_resource_and_reconciles_the_others()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        var client = new PassClient(
            Resource(generation: 3, name: "bad-version", version: "latest"),
            Resource(generation: 4, name: "blank-tenant", tenantId: " "),
            Resource(generation: 5, name: "bad-identity", uid: " "),
            Resource(generation: 6, name: "status-conflict"),
            Resource(generation: 7, name: "orders"),
            Resource(generation: 8, name: "billing"));
        client.FailStatusFor.Add("production/status-conflict");
        // One at a time, so a failure that escaped would visibly stop every later resource.
        var sut = Operator(store, provider, client, maximumConcurrency: 1);

        var results = await sut.ReconcileAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new (string DeploymentId, bool Succeeded, string? DiagnosticCode)[]
            {
                ("production/bad-version", false, "workload-version-invalid"),
                ("production/blank-tenant", false, "identifier-invalid"),
                ("production/bad-identity", false, "resource-invalid"),
                ("production/status-conflict", false, "reconcile-failed"),
                ("production/orders", true, null),
                ("production/billing", true, null),
            },
            results.Select(static result => (result.DeploymentId, result.Succeeded, result.DiagnosticCode)));
        Assert.Equal(ManagedDeploymentState.Failed, client.Statuses["production/bad-version"].State);
        Assert.Equal("workload-version-invalid", client.Statuses["production/bad-version"].DiagnosticCode);
        Assert.Equal(3, client.Statuses["production/bad-version"].ObservedResourceGeneration);
        Assert.Equal("identifier-invalid", client.Statuses["production/blank-tenant"].DiagnosticCode);
        Assert.Equal(ManagedDeploymentState.Ready, client.Statuses["production/orders"].State);
        Assert.Equal(ManagedDeploymentState.Ready, client.Statuses["production/billing"].State);
        Assert.NotNull(await store.GetAsync("production/orders", TestContext.Current.CancellationToken));
        Assert.NotNull(await store.GetAsync("production/billing", TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync("production/bad-version", TestContext.Current.CancellationToken));
        Assert.Equal(3, provider.ApplyCount);
    }

    [Fact]
    public async Task Reconcile_pass_over_the_api_client_isolates_an_invalid_custom_resource_spec()
    {
        var handler = new KubernetesApiHandler(
            ResourceJson(finalizers: [], name: "broken", version: "not-a-version"),
            ResourceJson(finalizers: [], name: "orders"));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kubernetes.default.svc"),
        };
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        var sut = Operator(
            store,
            provider,
            new KubernetesApiManagedDeploymentClient(http, "production"),
            maximumConcurrency: 1);

        var results = await sut.ReconcileAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("production/broken", results[0].DeploymentId);
        Assert.False(results[0].Succeeded);
        Assert.Equal("workload-version-invalid", results[0].DiagnosticCode);
        Assert.Equal("production/orders", results[1].DeploymentId);
        Assert.True(results[1].Succeeded);
        Assert.Equal(1, provider.ApplyCount);
        var brokenStatus = Assert.Single(handler.StatusBodies["broken"]);
        Assert.Contains("\"state\":\"Failed\"", brokenStatus, StringComparison.Ordinal);
        Assert.Contains("\"diagnosticCode\":\"workload-version-invalid\"", brokenStatus, StringComparison.Ordinal);
        var ordersStatus = Assert.Single(handler.StatusBodies["orders"]);
        Assert.Contains("\"state\":\"Ready\"", ordersStatus, StringComparison.Ordinal);
    }

    private static KubernetesManagedDeploymentOperator Operator(
        InMemoryManagedDeploymentStore store,
        RecordingProvider provider,
        IKubernetesManagedDeploymentClient client,
        int maximumConcurrency = 4)
    {
        var quotas = new ManagedDeploymentQuotaSource(
            store,
            new Dictionary<string, ManagedTenantQuota> { ["tenant-a"] = Quota });
        var controller = new ManagedDeploymentController(
            store,
            store,
            quotas,
            new ManagedInfrastructureProviderResolver([provider]),
            "operator-test");
        return new KubernetesManagedDeploymentOperator(store, controller, client, maximumConcurrency);
    }

    private static KubernetesManagedDeploymentResource Resource(
        long generation,
        IReadOnlyList<string>? finalizers = null,
        string version = "1.1.0",
        bool deleteProtection = false,
        bool deleting = false,
        string name = "orders",
        string tenantId = "tenant-a",
        string uid = "a5d083a4-1bf3-4351-8d94-84eb33b9b584") =>
        new(
            "production",
            name,
            uid,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            generation,
            deleting ? new DateTimeOffset(2026, 8, 29, 16, 0, 0, TimeSpan.Zero) : null,
            finalizers ?? [],
            new ManagedDeploymentSpec(
                "ignored",
                tenantId,
                "kubernetes",
                "uk-south",
                1,
                false,
                deleteProtection,
                [new ManagedWorkloadSpec(
                    ManagedWorkloadKind.Streams,
                    version,
                    new ManagedResourceRequest(2, 500, 512 * 1024 * 1024, 10L * 1024 * 1024 * 1024),
                    [new ManagedSecretReference("kubernetes", "orders-database")],
                    new Dictionary<string, string>())],
                new Dictionary<string, string> { ["environment"] = "production" }));

    private static string ResourceJson(
        IReadOnlyList<string> finalizers,
        string name = "orders",
        string version = "1.1.0") =>
        $$"""
        {
          "metadata": { "namespace": "production", "name": "{{name}}", "uid": "uid-1", "resourceVersion": "41", "generation": 7, "finalizers": {{System.Text.Json.JsonSerializer.Serialize(finalizers)}} },
          "spec": {
            "tenantId": "tenant-a", "provider": "kubernetes", "region": "uk-south",
            "paused": false, "deleteProtection": true, "labels": { "environment": "production" },
            "workloads": [{
              "kind": "Streams", "version": "{{version}}",
              "resources": { "replicas": 2, "cpuMillicoresPerReplica": 500, "memoryBytesPerReplica": 536870912, "storageBytes": 10737418240 },
              "secretReferences": [{ "store": "kubernetes", "name": "orders-database" }],
              "settings": { "mode": "durable" }
            }]
          }
        }
        """;

    private static HttpResponseMessage ListJsonResponse(string resource) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"metadata\":{\"continue\":null},\"items\":[" + resource + "]}",
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage ResourceJsonResponse(string resource) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(resource, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingClient(KubernetesManagedDeploymentResource resource) :
        IKubernetesManagedDeploymentClient
    {
        public KubernetesManagedDeploymentResource Resource { get; set; } = resource;

        public int FinalizerWrites { get; private set; }

        public KubernetesManagedDeploymentStatus? Status { get; private set; }

        public ValueTask<KubernetesManagedDeploymentPage> ListAsync(
            int limit,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KubernetesManagedDeploymentPage([Resource], null));

        public ValueTask<KubernetesManagedDeploymentResource> ReplaceFinalizersAsync(
            KubernetesManagedDeploymentResource item,
            IReadOnlyList<string> finalizers,
            CancellationToken cancellationToken = default)
        {
            FinalizerWrites++;
            Resource = new KubernetesManagedDeploymentResource(
                item.ResourceNamespace,
                item.Name,
                item.Uid,
                (long.Parse(item.ResourceVersion, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Generation,
                item.DeletionTimestamp,
                Array.AsReadOnly(finalizers.ToArray()),
                item.Desired);
            return ValueTask.FromResult(Resource);
        }

        public ValueTask ReplaceStatusAsync(
            KubernetesManagedDeploymentResource item,
            KubernetesManagedDeploymentStatus status,
            CancellationToken cancellationToken = default)
        {
            Status = status;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassClient(params KubernetesManagedDeploymentResource[] resources) :
        IKubernetesManagedDeploymentClient
    {
        public HashSet<string> FailStatusFor { get; } = new(StringComparer.Ordinal);

        public System.Collections.Concurrent.ConcurrentDictionary<string, KubernetesManagedDeploymentStatus> Statuses
        { get; } = new(StringComparer.Ordinal);

        public ValueTask<KubernetesManagedDeploymentPage> ListAsync(
            int limit,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KubernetesManagedDeploymentPage(resources, null));

        public ValueTask<KubernetesManagedDeploymentResource> ReplaceFinalizersAsync(
            KubernetesManagedDeploymentResource item,
            IReadOnlyList<string> finalizers,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KubernetesManagedDeploymentResource(
                item.ResourceNamespace,
                item.Name,
                item.Uid,
                item.ResourceVersion + "-finalized",
                item.Generation,
                item.DeletionTimestamp,
                Array.AsReadOnly(finalizers.ToArray()),
                item.Desired));

        public ValueTask ReplaceStatusAsync(
            KubernetesManagedDeploymentResource item,
            KubernetesManagedDeploymentStatus status,
            CancellationToken cancellationToken = default)
        {
            if (FailStatusFor.Contains(item.DeploymentId))
            {
                throw new HttpRequestException(
                    "The resourceVersion test failed.",
                    null,
                    HttpStatusCode.Conflict);
            }

            Statuses[item.DeploymentId] = status;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A minimal namespaced Kubernetes API for the BlueTuskDeployment resource.</summary>
    private sealed class KubernetesApiHandler(params string[] resources) : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public Dictionary<string, List<string>> StatusBodies { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            const string Collection = "/apis/controlplane.bluetusk.io/v1alpha1/namespaces/production/bluetuskdeployments";
            if (request.Method == HttpMethod.Get && path == Collection)
            {
                return ListJsonResponse(string.Join(",", resources));
            }

            Assert.Equal(HttpMethod.Patch, request.Method);
            var segments = path[(Collection.Length + 1)..].Split('/');
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (segments is [var statusName, "status"])
            {
                lock (_gate)
                {
                    if (!StatusBodies.TryGetValue(statusName, out var bodies))
                    {
                        StatusBodies[statusName] = bodies = [];
                    }

                    bodies.Add(body);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var name = Assert.Single(segments);
            var resource = Assert.Single(
                resources,
                candidate => candidate.Contains($"\"name\": \"{name}\"", StringComparison.Ordinal));
            return ResourceJsonResponse(resource.Replace(
                "\"finalizers\": []",
                "\"finalizers\": [\"" + KubernetesManagedDeploymentOperator.Finalizer + "\"]",
                StringComparison.Ordinal));
        }
    }

    private sealed class RecordingProvider : IManagedInfrastructureProvider
    {
        public string Name => "kubernetes";

        public int ApplyCount { get; private set; }

        public int DeleteCount { get; private set; }

        public ValueTask<ManagedDeploymentPlan> PlanAsync(
            ManagedDeploymentSpec desired,
            ManagedDeploymentStatus current,
            CancellationToken cancellationToken = default)
        {
            var desiredFingerprint = ManagedDeploymentValidation.GetFingerprint(desired);
            var planFingerprint = "plan-" + desiredFingerprint;
            return ValueTask.FromResult(new ManagedDeploymentPlan(
                desired.DeploymentId,
                desired.Generation,
                desiredFingerprint,
                planFingerprint,
                !string.Equals(current.AppliedPlanFingerprint, planFingerprint, StringComparison.Ordinal),
                [new ManagedDeploymentAction("apply", desired.DeploymentId, "Apply Kubernetes resources.")]));
        }

        public ValueTask<ManagedProviderResult> ApplyAsync(
            ManagedDeploymentSpec desired,
            ManagedDeploymentPlan plan,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return ValueTask.FromResult(
                new ManagedProviderResult("kubernetes/" + desired.DeploymentId, plan.PlanFingerprint));
        }

        public ValueTask DeleteAsync(
            ManagedDeploymentSpec desired,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.PathAndQuery, body));
            return _responses.Dequeue();
        }
    }
}
