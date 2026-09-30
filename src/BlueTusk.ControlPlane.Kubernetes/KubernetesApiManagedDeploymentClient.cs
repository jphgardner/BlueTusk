using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace BlueTusk.ControlPlane.Kubernetes;

/// <summary>Uses the Kubernetes REST API without taking ownership of authentication.</summary>
public sealed class KubernetesApiManagedDeploymentClient : IKubernetesManagedDeploymentClient
{
    public const string ApiGroup = "controlplane.bluetusk.io";
    public const string ApiVersion = "v1alpha1";
    public const string Plural = "bluetuskdeployments";

    private readonly HttpClient _httpClient;
    private readonly string _collectionPath;

    public KubernetesApiManagedDeploymentClient(
        HttpClient httpClient,
        string? resourceNamespace = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null)
        {
            throw new ArgumentException(
                "The Kubernetes HTTP client must have an absolute BaseAddress.",
                nameof(httpClient));
        }

        if (resourceNamespace is not null)
        {
            ValidatePathToken(resourceNamespace, 63, nameof(resourceNamespace));
        }

        _collectionPath = resourceNamespace is null
            ? $"/apis/{ApiGroup}/{ApiVersion}/{Plural}"
            : $"/apis/{ApiGroup}/{ApiVersion}/namespaces/{Uri.EscapeDataString(resourceNamespace)}/{Plural}";
    }

    public async ValueTask<KubernetesManagedDeploymentPage> ListAsync(
        int limit,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        if (continuationToken is { Length: > 4096 })
        {
            throw new ArgumentOutOfRangeException(nameof(continuationToken));
        }

        var path = _collectionPath + "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(continuationToken))
        {
            path += "&continue=" + Uri.EscapeDataString(continuationToken);
        }

        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync(
            stream,
            KubernetesManagedDeploymentJsonContext.Default.KubernetesResourceListDocument,
            cancellationToken).ConfigureAwait(false) ??
            throw new JsonException("Kubernetes returned an empty custom-resource list.");
        var resources = list.Items.Select(ToResource).ToArray();
        return new KubernetesManagedDeploymentPage(
            Array.AsReadOnly(resources),
            string.IsNullOrEmpty(list.Metadata.Continue) ? null : list.Metadata.Continue);
    }

    public async ValueTask<KubernetesManagedDeploymentResource> ReplaceFinalizersAsync(
        KubernetesManagedDeploymentResource resource,
        IReadOnlyList<string> finalizers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(finalizers);
        if (finalizers.Count > 32 || finalizers.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 253))
        {
            throw new ArgumentException("Kubernetes finalizers are invalid.", nameof(finalizers));
        }

        using var content = JsonPatch(writer =>
        {
            WritePatchValue(writer, "test", "/metadata/resourceVersion", resource.ResourceVersion);
            writer.WriteStartObject();
            writer.WriteString("op", "add");
            writer.WriteString("path", "/metadata/finalizers");
            writer.WriteStartArray("value");
            foreach (var finalizer in finalizers)
            {
                writer.WriteStringValue(finalizer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        using var response = await _httpClient.PatchAsync(ResourcePath(resource), content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var updated = await JsonSerializer.DeserializeAsync(
            stream,
            KubernetesManagedDeploymentJsonContext.Default.KubernetesResourceDocument,
            cancellationToken).ConfigureAwait(false) ??
            throw new JsonException("Kubernetes returned an empty custom resource after patching finalizers.");
        return ToResource(updated);
    }

    public async ValueTask ReplaceStatusAsync(
        KubernetesManagedDeploymentResource resource,
        KubernetesManagedDeploymentStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(status);
        using var content = JsonPatch(writer =>
        {
            WritePatchValue(writer, "test", "/metadata/resourceVersion", resource.ResourceVersion);
            writer.WriteStartObject();
            writer.WriteString("op", "add");
            writer.WriteString("path", "/status");
            writer.WriteStartObject("value");
            writer.WriteNumber("observedGeneration", status.ObservedResourceGeneration);
            writer.WriteNumber("managedGeneration", status.ManagedGeneration);
            writer.WriteString("state", status.State.ToString());
            if (status.DiagnosticCode is not null)
            {
                writer.WriteString("diagnosticCode", status.DiagnosticCode);
            }

            writer.WriteString("updatedAt", status.UpdatedAt);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        using var response = await _httpClient.PatchAsync(
            ResourcePath(resource) + "/status",
            content,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string ResourcePath(KubernetesManagedDeploymentResource resource)
    {
        ValidatePathToken(resource.ResourceNamespace, 63, nameof(resource));
        ValidatePathToken(resource.Name, 253, nameof(resource));
        return $"/apis/{ApiGroup}/{ApiVersion}/namespaces/{Uri.EscapeDataString(resource.ResourceNamespace)}/{Plural}/{Uri.EscapeDataString(resource.Name)}";
    }

    private static KubernetesManagedDeploymentResource ToResource(
        KubernetesResourceDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Metadata.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Metadata.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Metadata.Uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Metadata.ResourceVersion);
        if (document.Metadata.Generation <= 0)
        {
            throw new JsonException("Kubernetes custom-resource generation must be positive.");
        }

        var workloads = document.Spec.Workloads.Select(workload =>
        {
            if (!Enum.TryParse<ManagedWorkloadKind>(workload.Kind, ignoreCase: false, out var kind) ||
                !Enum.IsDefined(kind))
            {
                throw new JsonException($"Unsupported managed workload kind '{workload.Kind}'.");
            }

            return new ManagedWorkloadSpec(
                kind,
                workload.Version,
                new ManagedResourceRequest(
                    workload.Resources.Replicas,
                    workload.Resources.CpuMillicoresPerReplica,
                    workload.Resources.MemoryBytesPerReplica,
                    workload.Resources.StorageBytes),
                Array.AsReadOnly(
                    workload.SecretReferences.Select(static secret =>
                            new ManagedSecretReference(secret.Store, secret.Name, secret.Version))
                        .ToArray()),
                new Dictionary<string, string>(workload.Settings, StringComparer.Ordinal));
        }).ToArray();
        var desired = new ManagedDeploymentSpec(
            document.Metadata.Namespace + "/" + document.Metadata.Name,
            document.Spec.TenantId,
            document.Spec.Provider,
            document.Spec.Region,
            1,
            document.Spec.Paused,
            document.Spec.DeleteProtection,
            Array.AsReadOnly(workloads),
            new Dictionary<string, string>(document.Spec.Labels, StringComparer.Ordinal));
        ManagedDeploymentValidation.Validate(desired);
        return new KubernetesManagedDeploymentResource(
            document.Metadata.Namespace,
            document.Metadata.Name,
            document.Metadata.Uid,
            document.Metadata.ResourceVersion,
            document.Metadata.Generation,
            document.Metadata.DeletionTimestamp,
            Array.AsReadOnly(document.Metadata.Finalizers.ToArray()),
            desired);
    }

    private static ByteArrayContent JsonPatch(Action<Utf8JsonWriter> writeOperations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writeOperations(writer);
            writer.WriteEndArray();
        }

        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json")
        {
            CharSet = Encoding.UTF8.WebName,
        };
        return content;
    }

    private static void WritePatchValue(
        Utf8JsonWriter writer,
        string operation,
        string path,
        string value)
    {
        writer.WriteStartObject();
        writer.WriteString("op", operation);
        writer.WriteString("path", path);
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    private static void ValidatePathToken(string value, int maximumLength, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > maximumLength || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')))
        {
            throw new ArgumentException("Kubernetes path identity is invalid.", parameter);
        }
    }
}

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
    public KubernetesResourceMetadata Metadata { get; set; } = new();

    public KubernetesResourceSpec Spec { get; set; } = new();
}

internal sealed class KubernetesResourceMetadata
{
    public string Namespace { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Uid { get; set; } = string.Empty;

    public string ResourceVersion { get; set; } = string.Empty;

    public long Generation { get; set; }

    public DateTimeOffset? DeletionTimestamp { get; set; }

    public string[] Finalizers { get; set; } = [];
}

internal sealed class KubernetesResourceSpec
{
    public string TenantId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public bool Paused { get; set; }

    public bool DeleteProtection { get; set; }

    public KubernetesWorkloadDocument[] Workloads { get; set; } = [];

    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class KubernetesWorkloadDocument
{
    public string Kind { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public KubernetesResourceRequestDocument Resources { get; set; } = new();

    public KubernetesSecretReferenceDocument[] SecretReferences { get; set; } = [];

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class KubernetesResourceRequestDocument
{
    public int Replicas { get; set; }

    public int CpuMillicoresPerReplica { get; set; }

    public long MemoryBytesPerReplica { get; set; }

    public long StorageBytes { get; set; }
}

internal sealed class KubernetesSecretReferenceDocument
{
    public string Store { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(KubernetesResourceListDocument))]
[JsonSerializable(typeof(KubernetesResourceDocument))]
internal sealed partial class KubernetesManagedDeploymentJsonContext : JsonSerializerContext;
