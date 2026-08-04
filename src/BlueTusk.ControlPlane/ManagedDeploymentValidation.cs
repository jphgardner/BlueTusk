using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.ControlPlane;

/// <summary>Fail-closed validation and canonical fingerprinting for managed desired state.</summary>
public static class ManagedDeploymentValidation
{
    public const int MaximumWorkloads = 32;
    public const int MaximumReplicasPerWorkload = 256;
    public const int MaximumSecretReferencesPerWorkload = 128;
    public const int MaximumSettingsPerWorkload = 256;
    public const int MaximumLabels = 128;

    public static void Validate(ManagedDeploymentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidateToken(spec.DeploymentId, 128, nameof(spec.DeploymentId));
        ValidateToken(spec.TenantId, 128, nameof(spec.TenantId));
        ValidateToken(spec.Provider, 128, nameof(spec.Provider));
        ValidateToken(spec.Region, 128, nameof(spec.Region));
        if (spec.Generation <= 0)
        {
            throw Invalid("generation-invalid", "Generation must be greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(spec.Workloads);
        ArgumentNullException.ThrowIfNull(spec.Labels);
        if (spec.Workloads.Count is < 1 or > MaximumWorkloads)
        {
            throw Invalid(
                "workload-count-invalid",
                $"A deployment must contain between 1 and {MaximumWorkloads} workloads.");
        }

        var kinds = new HashSet<ManagedWorkloadKind>();
        foreach (var workload in spec.Workloads)
        {
            ArgumentNullException.ThrowIfNull(workload);
            if (!Enum.IsDefined(workload.Kind))
            {
                throw Invalid("workload-kind-invalid", "A workload kind is not supported.");
            }

            if (!kinds.Add(workload.Kind))
            {
                throw Invalid(
                    "workload-kind-duplicate",
                    $"Workload kind '{workload.Kind}' may occur only once.");
            }

            ValidateVersion(workload.Version);
            ValidateResources(workload.Resources);
            ValidateSecrets(workload.SecretReferences);
            ValidateMap(
                workload.Settings,
                MaximumSettingsPerWorkload,
                "settings-count-invalid",
                "setting");
        }

        ValidateMap(spec.Labels, MaximumLabels, "labels-count-invalid", "label");
    }

    public static string GetFingerprint(ManagedDeploymentSpec spec)
    {
        Validate(spec);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("deploymentId", spec.DeploymentId);
            writer.WriteString("tenantId", spec.TenantId);
            writer.WriteString("provider", spec.Provider);
            writer.WriteString("region", spec.Region);
            writer.WriteBoolean("paused", spec.Paused);
            writer.WriteBoolean("deleteProtection", spec.DeleteProtection);
            writer.WriteStartArray("workloads");
            foreach (var workload in spec.Workloads.OrderBy(static value => value.Kind))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", workload.Kind.ToString());
                writer.WriteString("version", workload.Version);
                writer.WriteNumber("replicas", workload.Resources.Replicas);
                writer.WriteNumber("cpu", workload.Resources.CpuMillicoresPerReplica);
                writer.WriteNumber("memory", workload.Resources.MemoryBytesPerReplica);
                writer.WriteNumber("storage", workload.Resources.StorageBytes);
                writer.WriteStartArray("secrets");
                foreach (var secret in workload.SecretReferences
                             .OrderBy(static value => value.Store, StringComparer.Ordinal)
                             .ThenBy(static value => value.Name, StringComparer.Ordinal)
                             .ThenBy(static value => value.Version, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("store", secret.Store);
                    writer.WriteString("name", secret.Name);
                    if (secret.Version is not null)
                    {
                        writer.WriteString("version", secret.Version);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                WriteMap(writer, "settings", workload.Settings);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteMap(writer, "labels", spec.Labels);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    public static ManagedTenantUsage GetRequestedUsage(ManagedDeploymentSpec spec)
    {
        Validate(spec);
        checked
        {
            return new ManagedTenantUsage(
                1,
                spec.Workloads.Sum(static workload => workload.Resources.Replicas),
                spec.Workloads.Sum(
                    static workload =>
                        (long)workload.Resources.Replicas *
                        workload.Resources.CpuMillicoresPerReplica),
                spec.Workloads.Sum(
                    static workload =>
                        (long)workload.Resources.Replicas *
                        workload.Resources.MemoryBytesPerReplica),
                spec.Workloads.Sum(static workload => workload.Resources.StorageBytes));
        }
    }

    public static void EnforceQuota(
        ManagedDeploymentSpec spec,
        ManagedTenantQuota quota,
        ManagedTenantUsage usage)
    {
        Validate(spec);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(usage);
        if (quota.MaximumDeployments < 0 ||
            quota.MaximumReplicas < 0 ||
            quota.MaximumCpuMillicores < 0 ||
            quota.MaximumMemoryBytes < 0 ||
            quota.MaximumStorageBytes < 0)
        {
            throw Invalid("quota-invalid", "Tenant quota values cannot be negative.");
        }

        if (usage.Deployments > quota.MaximumDeployments)
        {
            throw Quota("deployments");
        }

        if (usage.Replicas > quota.MaximumReplicas)
        {
            throw Quota("replicas");
        }

        if (usage.CpuMillicores > quota.MaximumCpuMillicores)
        {
            throw Quota("cpu");
        }

        if (usage.MemoryBytes > quota.MaximumMemoryBytes)
        {
            throw Quota("memory");
        }

        if (usage.StorageBytes > quota.MaximumStorageBytes)
        {
            throw Quota("storage");
        }
    }

    private static void ValidateResources(ManagedResourceRequest resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Replicas is < 1 or > MaximumReplicasPerWorkload)
        {
            throw Invalid(
                "replica-count-invalid",
                $"Replica count must be between 1 and {MaximumReplicasPerWorkload}.");
        }

        if (resources.CpuMillicoresPerReplica is < 10 or > 1_000_000)
        {
            throw Invalid(
                "cpu-request-invalid",
                "CPU per replica must be between 10 and 1,000,000 millicores.");
        }

        if (resources.MemoryBytesPerReplica is < 16 * 1024 * 1024L or > 16L * 1024 * 1024 * 1024 * 1024)
        {
            throw Invalid(
                "memory-request-invalid",
                "Memory per replica must be between 16 MiB and 16 TiB.");
        }

        if (resources.StorageBytes is < 0 or > 16L * 1024 * 1024 * 1024 * 1024)
        {
            throw Invalid(
                "storage-request-invalid",
                "Storage must be between zero and 16 TiB.");
        }
    }

    private static void ValidateSecrets(IReadOnlyList<ManagedSecretReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > MaximumSecretReferencesPerWorkload)
        {
            throw Invalid(
                "secret-count-invalid",
                $"A workload cannot reference more than {MaximumSecretReferencesPerWorkload} secrets.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ValidateToken(reference.Store, 128, nameof(reference.Store));
            ValidateToken(reference.Name, 1024, nameof(reference.Name));
            if (reference.Version is not null)
            {
                ValidateToken(reference.Version, 256, nameof(reference.Version));
            }

            if (!identities.Add(reference.Store + "\0" + reference.Name))
            {
                throw Invalid(
                    "secret-reference-duplicate",
                    "A workload cannot contain duplicate secret references.");
            }
        }
    }

    private static void ValidateMap(
        IReadOnlyDictionary<string, string> values,
        int maximum,
        string countCode,
        string description)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum)
        {
            throw Invalid(countCode, $"Too many {description} values.");
        }

        foreach (var pair in values)
        {
            ValidateToken(pair.Key, 128, description + " key");
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (pair.Value.Length > 4096)
            {
                throw Invalid(
                    description + "-value-too-long",
                    $"A {description} value cannot exceed 4096 characters.");
            }
        }
    }

    private static void ValidateVersion(string version)
    {
        ValidateToken(version, 128, nameof(version));
        if (!System.Version.TryParse(version.Split('-', 2)[0], out _))
        {
            throw Invalid(
                "workload-version-invalid",
                "Workload versions must begin with a numeric semantic version.");
        }
    }

    private static void ValidateToken(string value, int maximumLength, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maximumLength ||
            value.Any(static character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw Invalid(
                "identifier-invalid",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{parameter} must be a bounded printable value."));
        }
    }

    private static void WriteMap(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyDictionary<string, string> values)
    {
        writer.WriteStartObject(propertyName);
        foreach (var pair in values.OrderBy(static value => value.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static ManagedDeploymentValidationException Quota(string resource) =>
        Invalid("quota-" + resource + "-exceeded", $"Tenant {resource} quota would be exceeded.");

    private static ManagedDeploymentValidationException Invalid(string code, string message) =>
        new(code, message);
}
