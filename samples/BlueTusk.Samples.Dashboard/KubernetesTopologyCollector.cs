using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;

internal static class KubernetesTopologyCollectorHost
{
    internal static async Task RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = KubernetesTopologyCollectorOptions.FromEnvironment();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(static services =>
            KubernetesApiTopologySource.Create(
                services.GetRequiredService<KubernetesTopologyCollectorOptions>()));
        builder.Services.AddSingleton(static services =>
            new PostgreSqlTopologyStore(
                services.GetRequiredService<KubernetesTopologyCollectorOptions>()
                    .WriterConnectionString));
        builder.Services.AddHostedService<KubernetesTopologyCollectorWorker>();

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class KubernetesTopologyCollectorWorker(
    KubernetesApiTopologySource source,
    PostgreSqlTopologyStore store,
    KubernetesTopologyCollectorOptions options,
    ILogger<KubernetesTopologyCollectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.RefreshInterval;
            try
            {
                var snapshot = await source.CollectAsync(stoppingToken).ConfigureAwait(false);
                await store.ReplaceAsync(snapshot, stoppingToken).ConfigureAwait(false);
                TopologyCollectorLog.Synchronized(
                    logger,
                    snapshot.Nodes.Count,
                    snapshot.Edges.Count,
                    snapshot.ObservedAt);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                delay = options.FailureRetryInterval;
                TopologyCollectorLog.Failed(logger, exception);
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class TopologyCollectorLog
{
    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "Synchronized {NodeCount} live Kubernetes topology nodes and {EdgeCount} edges observed at {ObservedAt}.")]
    internal static partial void Synchronized(
        ILogger logger,
        int nodeCount,
        int edgeCount,
        DateTimeOffset observedAt);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Error,
        Message = "Live Kubernetes topology synchronization failed; the last complete database snapshot is retained.")]
    internal static partial void Failed(ILogger logger, Exception exception);
}

internal sealed class KubernetesTopologyCollectorOptions
{
    private KubernetesTopologyCollectorOptions(
        Uri apiServer,
        string tokenPath,
        string certificateAuthorityPath,
        IReadOnlyList<string> namespaces,
        string clusterId,
        string writerConnectionString,
        TimeSpan refreshInterval,
        TimeSpan failureRetryInterval)
    {
        ApiServer = apiServer;
        TokenPath = tokenPath;
        CertificateAuthorityPath = certificateAuthorityPath;
        Namespaces = namespaces;
        ClusterId = clusterId;
        WriterConnectionString = writerConnectionString;
        RefreshInterval = refreshInterval;
        FailureRetryInterval = failureRetryInterval;
    }

    internal Uri ApiServer { get; }

    internal string TokenPath { get; }

    internal string CertificateAuthorityPath { get; }

    internal IReadOnlyList<string> Namespaces { get; }

    internal string ClusterId { get; }

    internal string WriterConnectionString { get; }

    internal TimeSpan RefreshInterval { get; }

    internal TimeSpan FailureRetryInterval { get; }

    internal static KubernetesTopologyCollectorOptions FromEnvironment()
    {
        var host = RequiredEnvironment("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS") ?? "443";
        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) ||
            parsedPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("KUBERNETES_SERVICE_PORT_HTTPS is invalid.");
        }

        var namespaces = (Environment.GetEnvironmentVariable("BLUETUSK_TOPOLOGY_NAMESPACES") ??
                          "bluetusk-web,bluetusk-endurance,cert-manager")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (namespaces is not { Length: > 0 and <= 16 } ||
            namespaces.Any(static value => !IsDnsLabel(value)))
        {
            throw new InvalidOperationException("BLUETUSK_TOPOLOGY_NAMESPACES is invalid.");
        }

        var refreshSeconds = ParseBoundedSeconds(
            "BLUETUSK_TOPOLOGY_REFRESH_SECONDS",
            30,
            10,
            300);
        var retrySeconds = ParseBoundedSeconds(
            "BLUETUSK_TOPOLOGY_RETRY_SECONDS",
            10,
            5,
            60);
        var builder = new UriBuilder(Uri.UriSchemeHttps, host, parsedPort);
        return new KubernetesTopologyCollectorOptions(
            builder.Uri,
            Environment.GetEnvironmentVariable("BLUETUSK_KUBERNETES_TOKEN_PATH") ??
                "/var/run/secrets/kubernetes.io/serviceaccount/token",
            Environment.GetEnvironmentVariable("BLUETUSK_KUBERNETES_CA_PATH") ??
                "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt",
            Array.AsReadOnly(namespaces),
            RequiredEnvironment("BLUETUSK_KUBERNETES_CLUSTER_ID"),
            RequiredEnvironment("BLUETUSK_GRAPH_WRITER_CONNECTION_STRING"),
            TimeSpan.FromSeconds(refreshSeconds),
            TimeSpan.FromSeconds(retrySeconds));
    }

    private static int ParseBoundedSeconds(
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(text))
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            throw new InvalidOperationException($"{name} is outside its supported range.");
        }

        return value;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required.");

    private static bool IsDnsLabel(string value) =>
        value.Length is > 0 and <= 63 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

internal sealed class KubernetesApiTopologySource
{
    private const int PageSize = 500;
    private const int MaximumResourcesPerKindAndNamespace = 5_000;

    private static readonly KubernetesCollection[] Collections =
    [
        new("v1", "Pod", "api/v1", "pods"),
        new("v1", "Service", "api/v1", "services"),
        new("apps/v1", "Deployment", "apis/apps/v1", "deployments"),
        new("apps/v1", "ReplicaSet", "apis/apps/v1", "replicasets"),
        new("apps/v1", "StatefulSet", "apis/apps/v1", "statefulsets"),
        new("batch/v1", "Job", "apis/batch/v1", "jobs"),
        new("networking.k8s.io/v1", "Ingress", "apis/networking.k8s.io/v1", "ingresses"),
        new("discovery.k8s.io/v1", "EndpointSlice", "apis/discovery.k8s.io/v1", "endpointslices"),
        new("cert-manager.io/v1", "Certificate", "apis/cert-manager.io/v1", "certificates"),
    ];

    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, ValueTask<string>> _tokenProvider;
    private readonly IReadOnlyList<string> _namespaces;
    private readonly string _clusterId;

    internal KubernetesApiTopologySource(
        HttpClient httpClient,
        Func<CancellationToken, ValueTask<string>> tokenProvider,
        IReadOnlyList<string> namespaces,
        string clusterId)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _namespaces = namespaces ?? throw new ArgumentNullException(nameof(namespaces));
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        _clusterId = clusterId;
        if (_httpClient.BaseAddress is null)
        {
            throw new ArgumentException("The Kubernetes HTTP client requires a base address.", nameof(httpClient));
        }
    }

    internal static KubernetesApiTopologySource Create(KubernetesTopologyCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rootCertificate = X509Certificate2.CreateFromPemFile(options.CertificateAuthorityPath);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
                    ValidateServerCertificate(certificate, rootCertificate, policyErrors),
            },
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.ApiServer,
            Timeout = TimeSpan.FromSeconds(15),
        };
        return new KubernetesApiTopologySource(
            client,
            async cancellationToken =>
                (await File.ReadAllTextAsync(options.TokenPath, cancellationToken)
                    .ConfigureAwait(false)).Trim(),
            options.Namespaces,
            options.ClusterId);
    }

    internal async Task<DashboardTopologySnapshot> CollectAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("The Kubernetes service-account token is empty.");
        }

        var requests = from resourceNamespace in _namespaces
                       from collection in Collections
                       select FetchCollectionAsync(
                           resourceNamespace,
                           collection,
                           token,
                           cancellationToken);
        var pages = await Task.WhenAll(requests).ConfigureAwait(false);
        var resources = pages.SelectMany(static page => page).ToArray();
        return KubernetesTopologyGraphBuilder.Build(
            resources,
            _namespaces,
            _clusterId,
            DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyList<KubernetesResourceDocument>> FetchCollectionAsync(
        string resourceNamespace,
        KubernetesCollection collection,
        string token,
        CancellationToken cancellationToken)
    {
        var resources = new List<KubernetesResourceDocument>();
        string? continuationToken = null;
        do
        {
            var path = $"/{collection.ApiPath}/namespaces/{Uri.EscapeDataString(resourceNamespace)}/{collection.Plural}" +
                $"?limit={PageSize.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(continuationToken))
            {
                path += "&continue=" + Uri.EscapeDataString(continuationToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync(
                    stream,
                    KubernetesTopologyJsonContext.Default.KubernetesResourceListDocument,
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new JsonException("Kubernetes returned an empty resource list.");
            foreach (var resource in page.Items)
            {
                resource.ApiVersion = string.IsNullOrEmpty(resource.ApiVersion)
                    ? collection.ApiVersion
                    : resource.ApiVersion;
                resource.Kind = string.IsNullOrEmpty(resource.Kind)
                    ? collection.Kind
                    : resource.Kind;
                resource.Metadata.Namespace = string.IsNullOrEmpty(resource.Metadata.Namespace)
                    ? resourceNamespace
                    : resource.Metadata.Namespace;
                resources.Add(resource);
            }

            if (resources.Count > MaximumResourcesPerKindAndNamespace)
            {
                throw new InvalidOperationException(
                    "Kubernetes topology collection exceeded its bounded resource limit.");
            }

            continuationToken = page.Metadata.Continue;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        return resources.AsReadOnly();
    }

    private static bool ValidateServerCertificate(
        X509Certificate? certificate,
        X509Certificate2 rootCertificate,
        SslPolicyErrors policyErrors)
    {
        if (certificate is null ||
            (policyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                             SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using var serverCertificate = X509CertificateLoader.LoadCertificate(
            certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(serverCertificate);
    }

    private sealed record KubernetesCollection(
        string ApiVersion,
        string Kind,
        string ApiPath,
        string Plural);
}

internal static class KubernetesTopologyGraphBuilder
{
    internal static DashboardTopologySnapshot Build(
        IReadOnlyCollection<KubernetesResourceDocument> resources,
        IReadOnlyCollection<string> namespaces,
        string clusterId,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);

        var nodes = new Dictionary<string, DashboardTopologyNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, DashboardTopologyEdge>(StringComparer.Ordinal);
        var clusterNodeId = "kubernetes:cluster:" + clusterId;
        AddNode(nodes, new DashboardTopologyNode(
            clusterNodeId,
            clusterId,
            "Kubernetes cluster",
            "Observed",
            $"Live inventory across {namespaces.Count.ToString(CultureInfo.InvariantCulture)} namespaces",
            "Kubernetes API discovery scope",
            string.Empty,
            "v1",
            string.Empty,
            string.Empty,
            observedAt));

        foreach (var resourceNamespace in namespaces.Order(StringComparer.Ordinal))
        {
            var namespaceNodeId = NamespaceNodeId(resourceNamespace);
            AddNode(nodes, new DashboardTopologyNode(
                namespaceNodeId,
                resourceNamespace,
                "Namespace",
                "Observed",
                "Configured live topology observation scope",
                "Kubernetes API discovery configuration",
                resourceNamespace,
                "v1",
                string.Empty,
                string.Empty,
                observedAt));
            AddEdge(edges, clusterNodeId, namespaceNodeId, "CONTAINS", 1m, observedAt,
                "The observed Kubernetes cluster contains this namespace scope.");
        }

        var orderedResources = resources
            .OrderBy(static value => value.Kind, StringComparer.Ordinal)
            .ThenBy(static value => value.Metadata.Namespace, StringComparer.Ordinal)
            .ThenBy(static value => value.Metadata.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var resource in orderedResources)
        {
            ValidateResource(resource);
            var node = ToNode(resource, observedAt);
            AddNode(nodes, node);
            AddEdge(
                edges,
                NamespaceNodeId(resource.Metadata.Namespace),
                node.Id,
                "CONTAINS",
                1m,
                observedAt,
                $"Namespace {resource.Metadata.Namespace} contains this live {resource.Kind} object.");
        }

        foreach (var resource in orderedResources)
        {
            AddOwnershipEdges(resource, nodes, edges, observedAt);
            AddImageEdges(resource, nodes, edges, observedAt);
        }

        AddServiceSelectorEdges(orderedResources, nodes, edges, observedAt);
        AddIngressEdges(orderedResources, nodes, edges, observedAt);
        AddCertificateEdges(orderedResources, nodes, edges, observedAt);
        AddEndpointSliceEdges(orderedResources, nodes, edges, observedAt);
        AddExternalAddressEdges(orderedResources, nodes, edges, observedAt);

        var orderedNodes = nodes.Values.OrderBy(static value => value.Id, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.Values.OrderBy(static value => value.Id, StringComparer.Ordinal).ToArray();
        if (orderedNodes.Length > 1_000 || orderedEdges.Length > 2_000)
        {
            throw new InvalidOperationException(
                "The discovered topology exceeds the public dashboard's bounded graph limits.");
        }

        return new DashboardTopologySnapshot(
            Array.AsReadOnly(orderedNodes),
            Array.AsReadOnly(orderedEdges),
            observedAt);
    }

    private static DashboardTopologyNode ToNode(
        KubernetesResourceDocument resource,
        DateTimeOffset observedAt)
    {
        var (status, detail) = Describe(resource);
        return new DashboardTopologyNode(
            ResourceNodeId(resource.Kind, resource.Metadata.Namespace, resource.Metadata.Name),
            $"{resource.Metadata.Namespace}/{resource.Metadata.Name}",
            FriendlyKind(resource.Kind),
            status,
            detail,
            $"Live Kubernetes API: {resource.ApiVersion} {resource.Kind}; " +
            $"uid={resource.Metadata.Uid}; resourceVersion={resource.Metadata.ResourceVersion}",
            resource.Metadata.Namespace,
            resource.ApiVersion,
            resource.Metadata.Uid,
            resource.Metadata.ResourceVersion,
            observedAt);
    }

    private static (string Status, string Detail) Describe(KubernetesResourceDocument resource) =>
        resource.Kind switch
        {
            "Deployment" => DescribeReplicatedWorkload(resource, "availableReplicas"),
            "ReplicaSet" => DescribeReplicatedWorkload(resource, "readyReplicas"),
            "StatefulSet" => DescribeReplicatedWorkload(resource, "readyReplicas"),
            "Pod" => DescribePod(resource),
            "Service" => DescribeService(resource),
            "Ingress" => DescribeIngress(resource),
            "Certificate" => DescribeCertificate(resource),
            "Job" => DescribeJob(resource),
            "EndpointSlice" => DescribeEndpointSlice(resource),
            _ => ("Observed", $"Live {resource.ApiVersion} {resource.Kind} object"),
        };

    private static (string Status, string Detail) DescribeReplicatedWorkload(
        KubernetesResourceDocument resource,
        string readyProperty)
    {
        var desired = GetInt64(resource.Spec, "replicas");
        var ready = GetInt64(resource.Status, readyProperty);
        var status = desired == ready ? "Ready" : "Progressing";
        var images = ContainerImages(resource).Distinct(StringComparer.Ordinal).ToArray();
        var detail = $"{ready.ToString(CultureInfo.InvariantCulture)}/" +
            $"{desired.ToString(CultureInfo.InvariantCulture)} replicas ready";
        if (images.Length > 0)
        {
            detail += "; images=" + string.Join(", ", images);
        }

        return (status, detail);
    }

    private static (string Status, string Detail) DescribePod(KubernetesResourceDocument resource)
    {
        var phase = GetString(resource.Status, "phase") ?? "Unknown";
        var containers = GetArray(resource.Status, "containerStatuses").ToArray();
        var ready = containers.Count(static value => GetBoolean(value, "ready"));
        var restarts = containers.Sum(static value => GetInt64(value, "restartCount"));
        var node = GetString(resource.Spec, "nodeName") ?? "unscheduled";
        return (
            string.Equals(phase, "Running", StringComparison.Ordinal) && ready == containers.Length
                ? "Ready"
                : phase,
            $"phase={phase}; containers={ready.ToString(CultureInfo.InvariantCulture)}/" +
            $"{containers.Length.ToString(CultureInfo.InvariantCulture)} ready; " +
            $"restarts={restarts.ToString(CultureInfo.InvariantCulture)}; node={node}");
    }

    private static (string Status, string Detail) DescribeService(KubernetesResourceDocument resource)
    {
        var type = GetString(resource.Spec, "type") ?? "ClusterIP";
        var clusterIp = GetString(resource.Spec, "clusterIP") ?? "none";
        var ports = GetArray(resource.Spec, "ports")
            .Select(static value =>
            {
                var name = GetString(value, "name");
                var port = GetInt64(value, "port").ToString(CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(name) ? port : name + ":" + port;
            })
            .ToArray();
        return (
            "Active",
            $"type={type}; clusterIP={clusterIp}; ports={string.Join(", ", ports)}");
    }

    private static (string Status, string Detail) DescribeIngress(KubernetesResourceDocument resource)
    {
        var hosts = GetArray(resource.Spec, "rules")
            .Select(static value => GetString(value, "host"))
            .Where(static value => !string.IsNullOrEmpty(value))
            .ToArray();
        var ready = GetArray(GetObject(resource.Status, "loadBalancer"), "ingress").Any();
        return (ready ? "Ready" : "Pending", "hosts=" + string.Join(", ", hosts!));
    }

    private static (string Status, string Detail) DescribeCertificate(KubernetesResourceDocument resource)
    {
        var readyCondition = GetArray(resource.Status, "conditions")
            .FirstOrDefault(static value =>
                string.Equals(GetString(value, "type"), "Ready", StringComparison.Ordinal));
        var ready = GetString(readyCondition, "status");
        var reason = GetString(readyCondition, "reason") ?? "not-reported";
        var dnsNames = GetArray(resource.Spec, "dnsNames")
            .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(static value => !string.IsNullOrEmpty(value));
        return (
            string.Equals(ready, "True", StringComparison.Ordinal) ? "Ready" : "Not ready",
            $"reason={reason}; dnsNames={string.Join(", ", dnsNames!)}");
    }

    private static (string Status, string Detail) DescribeJob(KubernetesResourceDocument resource)
    {
        var failed = GetInt64(resource.Status, "failed");
        var active = GetInt64(resource.Status, "active");
        var succeeded = GetInt64(resource.Status, "succeeded");
        var status = failed > 0 ? "Failed" : active > 0 ? "Running" : succeeded > 0 ? "Complete" : "Pending";
        return (
            status,
            $"active={active.ToString(CultureInfo.InvariantCulture)}; " +
            $"succeeded={succeeded.ToString(CultureInfo.InvariantCulture)}; " +
            $"failed={failed.ToString(CultureInfo.InvariantCulture)}");
    }

    private static (string Status, string Detail) DescribeEndpointSlice(KubernetesResourceDocument resource)
    {
        var endpoints = GetArray(resource.Spec, "endpoints").ToArray();
        var ready = endpoints.Count(static value =>
            GetBoolean(GetObject(value, "conditions"), "ready", defaultValue: true));
        return (
            ready == endpoints.Length ? "Ready" : "Degraded",
            $"endpoints={endpoints.Length.ToString(CultureInfo.InvariantCulture)}; " +
            $"ready={ready.ToString(CultureInfo.InvariantCulture)}; " +
            $"addressType={GetString(resource.Spec, "addressType") ?? "unknown"}");
    }

    private static void AddOwnershipEdges(
        KubernetesResourceDocument resource,
        Dictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        var childId = ResourceNodeId(resource.Kind, resource.Metadata.Namespace, resource.Metadata.Name);
        foreach (var owner in resource.Metadata.OwnerReferences)
        {
            var ownerId = ResourceNodeId(owner.Kind, resource.Metadata.Namespace, owner.Name);
            if (!nodes.ContainsKey(ownerId))
            {
                continue;
            }

            AddEdge(
                edges,
                ownerId,
                childId,
                "OWNS",
                owner.Controller ? 3m : 2m,
                observedAt,
                $"Kubernetes ownerReference uid={owner.Uid}");
        }
    }

    private static void AddImageEdges(
        KubernetesResourceDocument resource,
        IDictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        var sourceId = ResourceNodeId(resource.Kind, resource.Metadata.Namespace, resource.Metadata.Name);
        foreach (var image in ContainerImages(resource).Distinct(StringComparer.Ordinal))
        {
            var imageId = "container-image:" + StableHash(image);
            AddNode(nodes, new DashboardTopologyNode(
                imageId,
                image,
                "Container image",
                image.Contains("@sha256:", StringComparison.Ordinal) ? "Digest pinned" : "Tag referenced",
                image,
                $"Derived from live {resource.Kind} {resource.Metadata.Namespace}/{resource.Metadata.Name}",
                resource.Metadata.Namespace,
                resource.ApiVersion,
                string.Empty,
                resource.Metadata.ResourceVersion,
                observedAt));
            AddEdge(
                edges,
                sourceId,
                imageId,
                "RUNS_IMAGE",
                2m,
                observedAt,
                $"Container specification references {image}.");
        }
    }

    private static void AddServiceSelectorEdges(
        IReadOnlyCollection<KubernetesResourceDocument> resources,
        IReadOnlyDictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        foreach (var service in resources.Where(static value => value.Kind == "Service"))
        {
            var selector = GetStringObject(GetObject(service.Spec, "selector"));
            if (selector.Count == 0)
            {
                continue;
            }

            foreach (var candidate in resources.Where(value =>
                         value.Metadata.Namespace == service.Metadata.Namespace &&
                         value.Kind is "Deployment" or "ReplicaSet" or "StatefulSet" or "Pod"))
            {
                var labels = candidate.Kind == "Pod"
                    ? candidate.Metadata.Labels
                    : GetStringObject(GetObject(
                        GetObject(GetObject(candidate.Spec, "template"), "metadata"),
                        "labels"));
                if (!selector.All(pair => labels.TryGetValue(pair.Key, out var value) && value == pair.Value))
                {
                    continue;
                }

                AddEdge(
                    edges,
                    ResourceNodeId("Service", service.Metadata.Namespace, service.Metadata.Name),
                    ResourceNodeId(candidate.Kind, candidate.Metadata.Namespace, candidate.Metadata.Name),
                    "SELECTS",
                    3m,
                    observedAt,
                    "The live Service selector matches this resource's labels.");
            }
        }
    }

    private static void AddIngressEdges(
        IReadOnlyCollection<KubernetesResourceDocument> resources,
        Dictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        foreach (var ingress in resources.Where(static value => value.Kind == "Ingress"))
        {
            var ingressId = ResourceNodeId("Ingress", ingress.Metadata.Namespace, ingress.Metadata.Name);
            foreach (var rule in GetArray(ingress.Spec, "rules"))
                foreach (var path in GetArray(GetObject(rule, "http"), "paths"))
                {
                    var serviceName = GetString(
                        GetObject(GetObject(path, "backend"), "service"),
                        "name");
                    if (string.IsNullOrEmpty(serviceName))
                    {
                        continue;
                    }

                    var serviceId = ResourceNodeId("Service", ingress.Metadata.Namespace, serviceName);
                    if (nodes.ContainsKey(serviceId))
                    {
                        AddEdge(
                            edges,
                            ingressId,
                            serviceId,
                            "ROUTES_TO",
                            3m,
                            observedAt,
                            $"host={GetString(rule, "host") ?? "*"}; path={GetString(path, "path") ?? "/"}");
                    }
                }
        }
    }

    private static void AddCertificateEdges(
        IReadOnlyCollection<KubernetesResourceDocument> resources,
        Dictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        foreach (var certificate in resources.Where(static value => value.Kind == "Certificate"))
        {
            var secretName = GetString(certificate.Spec, "secretName");
            if (string.IsNullOrEmpty(secretName))
            {
                continue;
            }

            foreach (var ingress in resources.Where(value =>
                         value.Kind == "Ingress" &&
                         value.Metadata.Namespace == certificate.Metadata.Namespace &&
                         GetArray(value.Spec, "tls").Any(tls =>
                             GetString(tls, "secretName") == secretName)))
            {
                var ingressId = ResourceNodeId("Ingress", ingress.Metadata.Namespace, ingress.Metadata.Name);
                if (nodes.ContainsKey(ingressId))
                {
                    AddEdge(
                        edges,
                        ResourceNodeId("Certificate", certificate.Metadata.Namespace, certificate.Metadata.Name),
                        ingressId,
                        "SECURES",
                        3m,
                        observedAt,
                        $"Certificate material is stored in Secret {secretName} used by this Ingress.");
                }
            }
        }
    }

    private static void AddEndpointSliceEdges(
        IReadOnlyCollection<KubernetesResourceDocument> resources,
        Dictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        foreach (var slice in resources.Where(static value => value.Kind == "EndpointSlice"))
        {
            var sliceId = ResourceNodeId("EndpointSlice", slice.Metadata.Namespace, slice.Metadata.Name);
            if (slice.Metadata.Labels.TryGetValue("kubernetes.io/service-name", out var serviceName))
            {
                var serviceId = ResourceNodeId("Service", slice.Metadata.Namespace, serviceName);
                if (nodes.ContainsKey(serviceId))
                {
                    AddEdge(
                        edges,
                        serviceId,
                        sliceId,
                        "DISCOVERS_ENDPOINTS_WITH",
                        2m,
                        observedAt,
                        "This live EndpointSlice belongs to the Service.");
                }
            }

            foreach (var endpoint in GetArray(slice.Spec, "endpoints"))
            {
                var target = GetObject(endpoint, "targetRef");
                var kind = GetString(target, "kind");
                var name = GetString(target, "name");
                var resourceNamespace = GetString(target, "namespace") ?? slice.Metadata.Namespace;
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var targetId = ResourceNodeId(kind, resourceNamespace, name);
                if (nodes.ContainsKey(targetId))
                {
                    AddEdge(
                        edges,
                        sliceId,
                        targetId,
                        "TARGETS",
                        3m,
                        observedAt,
                        "The live EndpointSlice endpoint targets this Kubernetes object.");
                }
            }
        }
    }

    private static void AddExternalAddressEdges(
        IReadOnlyCollection<KubernetesResourceDocument> resources,
        IDictionary<string, DashboardTopologyNode> nodes,
        IDictionary<string, DashboardTopologyEdge> edges,
        DateTimeOffset observedAt)
    {
        foreach (var service in resources.Where(static value => value.Kind == "Service"))
        {
            var serviceId = ResourceNodeId("Service", service.Metadata.Namespace, service.Metadata.Name);
            foreach (var ingress in GetArray(GetObject(service.Status, "loadBalancer"), "ingress"))
            {
                var address = GetString(ingress, "ip") ?? GetString(ingress, "hostname");
                if (string.IsNullOrEmpty(address))
                {
                    continue;
                }

                var addressId = "network-endpoint:" + StableHash(address);
                AddNode(nodes, new DashboardTopologyNode(
                    addressId,
                    address,
                    "External endpoint",
                    "Assigned",
                    $"Live load-balancer address assigned to {service.Metadata.Namespace}/{service.Metadata.Name}",
                    $"Derived from live Service status resourceVersion={service.Metadata.ResourceVersion}",
                    service.Metadata.Namespace,
                    service.ApiVersion,
                    string.Empty,
                    service.Metadata.ResourceVersion,
                    observedAt));
                AddEdge(
                    edges,
                    addressId,
                    serviceId,
                    "FORWARDS_TO",
                    3m,
                    observedAt,
                    "The Kubernetes load-balancer status exposes this Service at the observed address.");
            }
        }
    }

    private static IEnumerable<string> ContainerImages(KubernetesResourceDocument resource)
    {
        var podSpec = resource.Kind == "Pod"
            ? resource.Spec
            : GetObject(GetObject(resource.Spec, "template"), "spec");
        foreach (var property in new[] { "initContainers", "containers" })
            foreach (var container in GetArray(podSpec, property))
            {
                var image = GetString(container, "image");
                if (!string.IsNullOrEmpty(image))
                {
                    yield return image;
                }
            }
    }

    private static string FriendlyKind(string kind) => kind switch
    {
        "EndpointSlice" => "Endpoint slice",
        "ReplicaSet" => "Replica set",
        "StatefulSet" => "Stateful set",
        _ => kind,
    };

    private static void ValidateResource(KubernetesResourceDocument resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ApiVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.ResourceVersion);
    }

    private static string NamespaceNodeId(string resourceNamespace) =>
        "kubernetes:namespace:" + resourceNamespace;

    private static string ResourceNodeId(string kind, string resourceNamespace, string name) =>
        $"kubernetes:{kind.ToLowerInvariant()}:{resourceNamespace}:{name}";

    private static void AddNode(
        IDictionary<string, DashboardTopologyNode> nodes,
        DashboardTopologyNode node) => nodes.TryAdd(node.Id, node);

    private static void AddEdge(
        IDictionary<string, DashboardTopologyEdge> edges,
        string sourceId,
        string targetId,
        string kind,
        decimal weight,
        DateTimeOffset observedAt,
        string detail)
    {
        var id = "kubernetes-edge:" + StableHash(sourceId + "\0" + kind + "\0" + targetId);
        edges.TryAdd(id, new DashboardTopologyEdge(
            id,
            sourceId,
            targetId,
            kind,
            weight,
            observedAt,
            detail));
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonElement GetObject(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result)
            ? result
            : default;

    private static JsonElement.ArrayEnumerator GetArray(JsonElement value, string property)
    {
        var result = GetObject(value, property);
        return result.ValueKind == JsonValueKind.Array ? result.EnumerateArray() : default;
    }

    private static string? GetString(JsonElement value, string property)
    {
        var result = GetObject(value, property);
        return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    }

    private static long GetInt64(JsonElement value, string property)
    {
        var result = GetObject(value, property);
        return result.ValueKind == JsonValueKind.Number && result.TryGetInt64(out var number)
            ? number
            : 0;
    }

    private static bool GetBoolean(JsonElement value, string property, bool defaultValue = false)
    {
        var result = GetObject(value, property);
        return result.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static Dictionary<string, string> GetStringObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return value.EnumerateObject()
            .Where(static property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.GetString()!,
                StringComparer.Ordinal);
    }
}

internal sealed class PostgreSqlTopologyStore(string connectionString)
{
    internal async Task ReplaceAsync(
        DashboardTopologySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE TEMP TABLE bt_topology_entities
                    (LIKE bluetusk_dashboard.graph_entities INCLUDING DEFAULTS)
                    ON COMMIT DROP;
                CREATE TEMP TABLE bt_topology_relationships
                    (LIKE bluetusk_dashboard.graph_relationships INCLUDING DEFAULTS)
                    ON COMMIT DROP;
                """,
                cancellationToken).ConfigureAwait(false);
            await InsertNodesAsync(connection, transaction, snapshot.Nodes, cancellationToken)
                .ConfigureAwait(false);
            await InsertEdgesAsync(connection, transaction, snapshot.Edges, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO bluetusk_dashboard.graph_entities
                    (id, display_name, kind, status, detail, provenance, resource_namespace,
                     api_version, resource_uid, resource_version, observed_at)
                SELECT id, display_name, kind, status, detail, provenance, resource_namespace,
                       api_version, resource_uid, resource_version, observed_at
                FROM bt_topology_entities
                ON CONFLICT (id) DO UPDATE SET
                    display_name = EXCLUDED.display_name,
                    kind = EXCLUDED.kind,
                    status = EXCLUDED.status,
                    detail = EXCLUDED.detail,
                    provenance = EXCLUDED.provenance,
                    resource_namespace = EXCLUDED.resource_namespace,
                    api_version = EXCLUDED.api_version,
                    resource_uid = EXCLUDED.resource_uid,
                    resource_version = EXCLUDED.resource_version,
                    observed_at = EXCLUDED.observed_at;

                INSERT INTO bluetusk_dashboard.graph_relationships
                    (id, source_id, target_id, kind, weight, observed_at, detail)
                SELECT id, source_id, target_id, kind, weight, observed_at, detail
                FROM bt_topology_relationships
                ON CONFLICT (id) DO UPDATE SET
                    source_id = EXCLUDED.source_id,
                    target_id = EXCLUDED.target_id,
                    kind = EXCLUDED.kind,
                    weight = EXCLUDED.weight,
                    observed_at = EXCLUDED.observed_at,
                    detail = EXCLUDED.detail;

                DELETE FROM bluetusk_dashboard.graph_relationships AS existing
                WHERE NOT EXISTS (
                    SELECT 1 FROM bt_topology_relationships AS current
                    WHERE current.id = existing.id);
                DELETE FROM bluetusk_dashboard.graph_entities AS existing
                WHERE NOT EXISTS (
                    SELECT 1 FROM bt_topology_entities AS current
                    WHERE current.id = existing.id);
                """,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task InsertNodesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<DashboardTopologyNode> nodes,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO bt_topology_entities
                (id, display_name, kind, status, detail, provenance, resource_namespace,
                 api_version, resource_uid, resource_version, observed_at)
            VALUES
                (@id, @display_name, @kind, @status, @detail, @provenance, @resource_namespace,
                 @api_version, @resource_uid, @resource_version, @observed_at)
            """;
        var id = AddParameter(command, "id", DbType.String);
        var displayName = AddParameter(command, "display_name", DbType.String);
        var kind = AddParameter(command, "kind", DbType.String);
        var status = AddParameter(command, "status", DbType.String);
        var detail = AddParameter(command, "detail", DbType.String);
        var provenance = AddParameter(command, "provenance", DbType.String);
        var resourceNamespace = AddParameter(command, "resource_namespace", DbType.String);
        var apiVersion = AddParameter(command, "api_version", DbType.String);
        var resourceUid = AddParameter(command, "resource_uid", DbType.String);
        var resourceVersion = AddParameter(command, "resource_version", DbType.String);
        var observedAt = AddParameter(command, "observed_at", DbType.DateTimeOffset);
        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
        foreach (var node in nodes)
        {
            id.Value = node.Id;
            displayName.Value = node.DisplayName;
            kind.Value = node.Kind;
            status.Value = node.Status;
            detail.Value = node.Detail;
            provenance.Value = node.Provenance;
            resourceNamespace.Value = node.Namespace;
            apiVersion.Value = node.ApiVersion;
            resourceUid.Value = node.ResourceUid;
            resourceVersion.Value = node.ResourceVersion;
            observedAt.Value = node.ObservedAt;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertEdgesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<DashboardTopologyEdge> edges,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO bt_topology_relationships
                (id, source_id, target_id, kind, weight, observed_at, detail)
            VALUES (@id, @source_id, @target_id, @kind, @weight, @observed_at, @detail)
            """;
        var id = AddParameter(command, "id", DbType.String);
        var sourceId = AddParameter(command, "source_id", DbType.String);
        var targetId = AddParameter(command, "target_id", DbType.String);
        var kind = AddParameter(command, "kind", DbType.String);
        var weight = AddParameter(command, "weight", DbType.Decimal);
        var observedAt = AddParameter(command, "observed_at", DbType.DateTimeOffset);
        var detail = AddParameter(command, "detail", DbType.String);
        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
        foreach (var edge in edges)
        {
            id.Value = edge.Id;
            sourceId.Value = edge.SourceId;
            targetId.Value = edge.TargetId;
            kind.Value = edge.Kind;
            weight.Value = edge.Weight;
            observedAt.Value = edge.ObservedAt;
            detail.Value = edge.Detail;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbParameter AddParameter(DbCommand command, string name, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        _ = command.Parameters.Add(parameter);
        return parameter;
    }
}

internal sealed record DashboardTopologySnapshot(
    IReadOnlyList<DashboardTopologyNode> Nodes,
    IReadOnlyList<DashboardTopologyEdge> Edges,
    DateTimeOffset ObservedAt);

internal sealed record DashboardTopologyNode(
    string Id,
    string DisplayName,
    string Kind,
    string Status,
    string Detail,
    string Provenance,
    string Namespace,
    string ApiVersion,
    string ResourceUid,
    string ResourceVersion,
    DateTimeOffset ObservedAt);

internal sealed record DashboardTopologyEdge(
    string Id,
    string SourceId,
    string TargetId,
    string Kind,
    decimal Weight,
    DateTimeOffset ObservedAt,
    string Detail);

internal sealed class KubernetesResourceListDocument
{
    public KubernetesListMetadata Metadata { get; set; } = new();

    public KubernetesResourceDocument[] Items { get; set; } = [];
}

internal sealed class KubernetesListMetadata
{
    public string? Continue { get; set; }
}

internal sealed class KubernetesResourceDocument
{
    public string ApiVersion { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public KubernetesResourceMetadata Metadata { get; set; } = new();

    public JsonElement Spec { get; set; }

    public JsonElement Status { get; set; }
}

internal sealed class KubernetesResourceMetadata
{
    public string Namespace { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Uid { get; set; } = string.Empty;

    public string ResourceVersion { get; set; } = string.Empty;

    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);

    public KubernetesOwnerReference[] OwnerReferences { get; set; } = [];
}

internal sealed class KubernetesOwnerReference
{
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Uid { get; set; } = string.Empty;

    public bool Controller { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(KubernetesResourceListDocument))]
internal sealed partial class KubernetesTopologyJsonContext : JsonSerializerContext;
