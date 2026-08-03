using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Subscriptions.Internal;

internal static partial class BlueTuskSubscriptionMetadata
{
    public const string AnnotationName = "BlueTusk:Subscriptions";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static BlueTuskSubscriptionDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        ArgumentNullException.ThrowIfNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskSubscriptionDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskSubscriptionDefinitionSet definitions)
    {
        ValidateForModel(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static string Serialize(BlueTuskSubscriptionDefinition definition)
    {
        ValidateForModel(definition);
        return JsonSerializer.Serialize(Normalize(definition), SerializerOptions);
    }

    public static BlueTuskSubscriptionDefinitionSet Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskSubscriptionDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The subscription definition set is empty.", nameof(json));
        ValidateForModel(definitions);
        return Normalize(definitions);
    }

    public static BlueTuskSubscriptionDefinition DeserializeDefinition(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<BlueTuskSubscriptionDefinition>(json, SerializerOptions)
            ?? throw new ArgumentException("The subscription definition is empty.", nameof(json));
        ValidateForModel(definition);
        return Normalize(definition);
    }

    public static void ValidateForModel(BlueTuskSubscriptionDefinitionSet definitions)
    {
        Validate(definitions);
        foreach (var definition in definitions.Subscriptions)
        {
            RejectSensitiveModelConnection(definition);
        }
    }

    public static void ValidateForModel(BlueTuskSubscriptionDefinition definition)
    {
        Validate(definition);
        RejectSensitiveModelConnection(definition);
    }

    public static void ValidateForCreate(BlueTuskSubscriptionDefinition definition)
    {
        Validate(definition);
        if (definition.Failover && definition.SlotName is null)
        {
            throw new ArgumentException(
                "Creating a failover-enabled subscription requires a slot name.",
                nameof(definition));
        }
    }

    public static void Validate(BlueTuskSubscriptionDefinitionSet definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(definitions.Subscriptions);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions.Subscriptions)
        {
            Validate(definition);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException($"Subscription '{definition.Name}' is configured more than once.");
            }
        }
    }

    public static void Validate(BlueTuskSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentNullException.ThrowIfNull(definition.Connection);
        if (!Enum.IsDefined(definition.Connection.Kind) ||
            !Enum.IsDefined(definition.Streaming) ||
            !Enum.IsDefined(definition.SynchronousCommit) ||
            !Enum.IsDefined(definition.Origin))
        {
            throw new ArgumentException("The subscription uses an unknown enum value.", nameof(definition));
        }

        if (definition.Connection.Kind == BlueTuskSubscriptionConnectionKind.Redacted)
        {
            if (definition.Connection.Value is not null)
            {
                throw new ArgumentException("A redacted subscription connection cannot contain a value.");
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Connection.Value);
        }

        ArgumentNullException.ThrowIfNull(definition.Publications);
        if (definition.Publications.Count == 0)
        {
            throw new ArgumentException("A subscription requires at least one publication.");
        }

        var publications = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publication in definition.Publications)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(publication);
            if (!publications.Add(publication))
            {
                throw new ArgumentException($"Subscription publication '{publication}' is configured more than once.");
            }
        }

        if (definition.SlotName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.SlotName);
        }

        if (definition.MaxRetentionDuration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Subscription maximum retention duration cannot be negative.");
        }

        if (definition.WalReceiverTimeout is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.WalReceiverTimeout);
        }

        if (definition.Connection.Kind != BlueTuskSubscriptionConnectionKind.Redacted)
        {
            if (!definition.ConnectOnCreate &&
                (definition.CreateSlot || definition.CopyData || definition.Enabled))
            {
                throw new ArgumentException(
                    "A disconnected subscription create cannot create a slot, copy data, or start enabled.");
            }

            if (definition.CreateSlot && definition.SlotName is null)
            {
                throw new ArgumentException("Creating a subscription slot requires a slot name.");
            }
        }

        if (definition.Enabled && definition.SlotName is null)
        {
            throw new ArgumentException("An enabled subscription requires an associated slot.");
        }

        if (definition.MaxRetentionDuration > 0 && !definition.RetainDeadTuples)
        {
            throw new ArgumentException(
                "A maximum retention duration requires retain_dead_tuples to be enabled.");
        }
    }

    public static BlueTuskSubscriptionDefinitionSet Normalize(BlueTuskSubscriptionDefinitionSet definitions) =>
        new(definitions.Subscriptions.Select(Normalize)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskSubscriptionDefinition Normalize(BlueTuskSubscriptionDefinition definition) =>
        definition with
        {
            Connection = definition.Connection with { Value = definition.Connection.Value?.Trim() },
            Publications = definition.Publications.Order(StringComparer.Ordinal).ToArray(),
            WalReceiverTimeout = definition.WalReceiverTimeout is "-1" ? null : definition.WalReceiverTimeout?.Trim(),
        };

    public static int MinimumServerVersion(BlueTuskSubscriptionDefinition definition)
    {
        if (definition.Connection.Kind == BlueTuskSubscriptionConnectionKind.ForeignServer ||
            definition.RetainDeadTuples || definition.MaxRetentionDuration > 0 ||
            definition.WalReceiverTimeout is not null)
        {
            return 190000;
        }

        if (definition.Failover)
        {
            return 170000;
        }

        return definition.Streaming == BlueTuskSubscriptionStreamingMode.Parallel ||
               !definition.PasswordRequired || definition.RunAsOwner ||
               definition.Origin != BlueTuskSubscriptionOrigin.Any
            ? 160000
            : 150000;
    }

    public static bool ContainsSensitiveConnectionString(string value)
    {
        if (SensitiveKeywordRegex().IsMatch(value))
        {
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("postgresql", StringComparison.OrdinalIgnoreCase)) &&
               uri.UserInfo.Contains(':', StringComparison.Ordinal);
    }

    private static void RejectSensitiveModelConnection(BlueTuskSubscriptionDefinition definition)
    {
        if (definition.Connection is
            {
                Kind: BlueTuskSubscriptionConnectionKind.ConnectionString,
                Value: { } value,
            } && ContainsSensitiveConnectionString(value))
        {
            throw new ArgumentException(
                "Subscription connection strings containing password credentials cannot be stored in EF model " +
                "metadata or generated C#. Supply them only from a secret source in a manually authored migration.",
                nameof(definition));
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [GeneratedRegex(@"(?:^|[\s;?&])(?:password|pwd|sslpassword)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKeywordRegex();
}
