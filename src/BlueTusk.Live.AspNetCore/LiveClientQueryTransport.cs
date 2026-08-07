using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

namespace BlueTusk.Live.AspNetCore;

/// <summary>A capability grant returned only after application authorization.</summary>
public sealed class LiveClientQueryGrant
{
    public LiveClientQueryGrant(
        DbDataSource dataSource,
        LiveClientQueryPolicy policy,
        LiveSecurityScope securityScope)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(securityScope);
        DataSource = dataSource;
        Policy = policy;
        SecurityScope = securityScope;
    }

    public DbDataSource DataSource { get; }

    public LiveClientQueryPolicy Policy { get; }

    public LiveSecurityScope SecurityScope { get; }
}

/// <summary>
/// Authorizes one client-authored query against an application-owned policy and data source.
/// </summary>
public interface ILiveClientQueryAuthorizer
{
    ValueTask<LiveClientQueryGrant?> AuthorizeAsync(
        string capability,
        LiveClientQueryDefinition definition,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves capability-secured client SQL or remote LINQ through the ordinary Live transport contract.
/// </summary>
public sealed class LiveClientQueryTransportResolver : ILiveTransportSubscriptionResolver
{
    private readonly ILiveClientQueryAuthorizer _authorizer;
    private readonly ILiveInvalidationLog _invalidationLog;
    private readonly ILiveReplayStore _replayStore;
    private readonly LiveSharedSubscriptionRegistry _registry;
    private readonly LiveSharedSubscriptionOptions _subscriptionOptions;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startGates =
        new(StringComparer.Ordinal);

    public LiveClientQueryTransportResolver(
        ILiveClientQueryAuthorizer authorizer,
        ILiveInvalidationLog invalidationLog,
        ILiveReplayStore replayStore,
        LiveSharedSubscriptionRegistry registry,
        LiveSharedSubscriptionOptions? subscriptionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(invalidationLog);
        ArgumentNullException.ThrowIfNull(replayStore);
        ArgumentNullException.ThrowIfNull(registry);
        _authorizer = authorizer;
        _invalidationLog = invalidationLog;
        _replayStore = replayStore;
        _registry = registry;
        _subscriptionOptions = subscriptionOptions ?? new LiveSharedSubscriptionOptions();
    }

    public async ValueTask<ILiveSharedSubscription> ResolveAsync(
        string query,
        JsonElement parameters,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(principal);
        ClientQueryEnvelope envelope;
        try
        {
            envelope = ClientQueryEnvelope.Parse(query, parameters);
        }
        catch (LiveTransportRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException or OverflowException)
        {
            throw new LiveTransportRequestException(
                $"Client query capability '{query}' contains an invalid query document: {exception.Message}");
        }

        var grant = await _authorizer.AuthorizeAsync(
            query,
            envelope.Definition,
            principal,
            cancellationToken).ConfigureAwait(false);
        if (grant is null)
        {
            throw new LiveTransportAuthorizationException(
                $"The authenticated principal is not authorized for client-query capability '{query}'.");
        }

        LiveQueryPlan<LiveClientRow, string> plan;
        LiveQueryArguments arguments;
        try
        {
            plan = LiveClientQueryCompiler.Compile(
                grant.DataSource,
                grant.Policy,
                envelope.Definition);
            arguments = plan.Bind(envelope.Values);
        }
        catch (LiveClientQueryException exception)
        {
            throw new LiveTransportRequestException(exception.Message);
        }
        catch (ArgumentException exception)
        {
            throw new LiveTransportRequestException(exception.Message);
        }

        var session = new LiveQuerySession<LiveClientRow, string>(
            plan,
            arguments,
            grant.SecurityScope,
            _invalidationLog);
        var candidate = new LiveSharedSubscription<LiveClientRow, string>(
            session,
            _replayStore,
            _subscriptionOptions);
        var gate = _startGates.GetOrAdd(
            session.Identity.Fingerprint,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveSharedSubscription<LiveClientRow, string> selected;
            try
            {
                selected = _registry.GetOrAdd(candidate);
            }
            catch
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (!ReferenceEquals(selected, candidate))
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            if (!selected.Status.IsStarted)
            {
                try
                {
                    await selected.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _ = await _registry.RemoveAsync(selected.Identity).ConfigureAwait(false);
                    throw;
                }
            }

            return selected;
        }
        finally
        {
            gate.Release();
            _startGates.TryRemove(
                new KeyValuePair<string, SemaphoreSlim>(
                    session.Identity.Fingerprint,
                    gate));
        }
    }

    private sealed record ClientQueryEnvelope(
        LiveClientQueryDefinition Definition,
        IReadOnlyDictionary<string, object?> Values)
    {
        private static readonly HashSet<string> AllowedProperties =
        [
            "language",
            "sql",
            "linq",
            "keyColumns",
            "maximumResultCount",
            "parameters",
        ];

        public static ClientQueryEnvelope Parse(
            string capability,
            JsonElement root)
        {
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new LiveTransportRequestException(
                    "A client-query document must be a JSON object.");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name))
                {
                    throw new LiveTransportRequestException(
                        $"Unknown client-query property '{property.Name}'.");
                }
            }

            var language = RequiredString(root, "language");
            var maximumResultCount = RequiredInt32(root, "maximumResultCount");
            var keyColumns = RequiredStringArray(root, "keyColumns");
            var (parameterDefinitions, values) = ParseParameters(root);
            LiveClientQueryDefinition definition;
            if (string.Equals(language, "sql", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("linq", out _))
                {
                    throw new LiveTransportRequestException(
                        "A SQL client query cannot also contain a LINQ document.");
                }

                definition = LiveClientQueryDefinition.CreateSql(
                    $"client:{capability}",
                    "transport-v1",
                    RequiredString(root, "sql"),
                    parameterDefinitions,
                    keyColumns,
                    maximumResultCount);
            }
            else if (string.Equals(language, "linq", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("sql", out _))
                {
                    throw new LiveTransportRequestException(
                        "A LINQ client query cannot also contain SQL.");
                }

                definition = LiveClientQueryDefinition.CreateLinq(
                    $"client:{capability}",
                    "transport-v1",
                    ParseLinq(root),
                    parameterDefinitions,
                    keyColumns,
                    maximumResultCount);
            }
            else
            {
                throw new LiveTransportRequestException(
                    $"Unknown client-query language '{language}'.");
            }

            return new ClientQueryEnvelope(definition, values);
        }

        private static LiveClientLinqQuery ParseLinq(JsonElement root)
        {
            if (!root.TryGetProperty("linq", out var linq) ||
                linq.ValueKind is not JsonValueKind.Object)
            {
                throw new LiveTransportRequestException(
                    "A remote LINQ query requires a LINQ object.");
            }

            RequireOnly(
                linq,
                "linq",
                ["schema", "table", "columns", "filters", "orderings"]);
            var filters = new List<LiveClientFilter>();
            if (!linq.TryGetProperty("filters", out var filterArray) ||
                filterArray.ValueKind is not JsonValueKind.Array)
            {
                throw new LiveTransportRequestException(
                    "Remote LINQ filters must be an array.");
            }

            foreach (var filter in filterArray.EnumerateArray())
            {
                RequireOnly(
                    filter,
                    "filter",
                    ["column", "operator", "parameter"]);
                var operatorName = RequiredString(filter, "operator");
                if (!Enum.TryParse<LiveClientFilterOperator>(
                        operatorName,
                        ignoreCase: true,
                        out var @operator) ||
                    !Enum.IsDefined(@operator))
                {
                    throw new LiveTransportRequestException(
                        $"Unknown remote LINQ filter operator '{operatorName}'.");
                }

                filters.Add(new LiveClientFilter(
                    RequiredString(filter, "column"),
                    @operator,
                    filter.TryGetProperty("parameter", out var parameter)
                        ? RequiredStringValue(parameter, "parameter")
                        : null));
            }

            var orderings = new List<LiveClientOrdering>();
            if (!linq.TryGetProperty("orderings", out var orderingArray) ||
                orderingArray.ValueKind is not JsonValueKind.Array)
            {
                throw new LiveTransportRequestException(
                    "Remote LINQ orderings must be an array.");
            }

            foreach (var ordering in orderingArray.EnumerateArray())
            {
                RequireOnly(
                    ordering,
                    "ordering",
                    ["column", "direction"]);
                var directionName = RequiredString(ordering, "direction");
                if (!Enum.TryParse<LiveClientSortDirection>(
                        directionName,
                        ignoreCase: true,
                        out var direction) ||
                    !Enum.IsDefined(direction))
                {
                    throw new LiveTransportRequestException(
                        $"Unknown remote LINQ sort direction '{directionName}'.");
                }

                orderings.Add(new LiveClientOrdering(
                    RequiredString(ordering, "column"),
                    direction));
            }

            return new LiveClientLinqQuery(
                RequiredString(linq, "schema"),
                RequiredString(linq, "table"),
                RequiredStringArray(linq, "columns"),
                filters,
                orderings);
        }

        private static (
            IReadOnlyList<LiveQueryParameter> Definitions,
            IReadOnlyDictionary<string, object?> Values)
            ParseParameters(JsonElement root)
        {
            if (!root.TryGetProperty("parameters", out var parameters) ||
                parameters.ValueKind is not JsonValueKind.Object)
            {
                throw new LiveTransportRequestException(
                    "Client-query parameters must be an object.");
            }

            var definitions = new List<LiveQueryParameter>();
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in parameters.EnumerateObject()
                         .OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                if (property.Value.ValueKind is not JsonValueKind.Object)
                {
                    throw new LiveTransportRequestException(
                        $"Client-query parameter '{property.Name}' must be an object.");
                }

                RequireOnly(
                    property.Value,
                    $"parameter '{property.Name}'",
                    ["type", "allowNull", "value"]);
                var typeName = RequiredString(property.Value, "type");
                var type = ParseType(typeName);
                var allowNull = property.Value.TryGetProperty("allowNull", out var allowNullElement) &&
                    allowNullElement.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new LiveTransportRequestException(
                            $"Client-query parameter '{property.Name}' allowNull must be boolean."),
                    };
                if (!property.Value.TryGetProperty("value", out var value))
                {
                    throw new LiveTransportRequestException(
                        $"Client-query parameter '{property.Name}' has no value.");
                }

                definitions.Add(new LiveQueryParameter(property.Name, type, allowNull));
                values.Add(
                    property.Name,
                    ParseValue(property.Name, type, allowNull, value));
            }

            return (definitions, values);
        }

        private static object? ParseValue(
            string name,
            Type type,
            bool allowNull,
            JsonElement value)
        {
            if (value.ValueKind is JsonValueKind.Null)
            {
                if (!allowNull)
                {
                    throw new LiveTransportRequestException(
                        $"Client-query parameter '{name}' cannot be null.");
                }

                return null;
            }

            try
            {
                if (type == typeof(string))
                {
                    return value.GetString() ??
                        throw new FormatException("String is null.");
                }

                if (type == typeof(bool))
                {
                    return value.GetBoolean();
                }

                if (type == typeof(byte))
                {
                    return value.GetByte();
                }

                if (type == typeof(sbyte))
                {
                    return value.GetSByte();
                }

                if (type == typeof(short))
                {
                    return value.GetInt16();
                }

                if (type == typeof(ushort))
                {
                    return value.GetUInt16();
                }

                if (type == typeof(int))
                {
                    return value.GetInt32();
                }

                if (type == typeof(uint))
                {
                    return value.GetUInt32();
                }

                if (type == typeof(long))
                {
                    return value.GetInt64();
                }

                if (type == typeof(ulong))
                {
                    return value.GetUInt64();
                }

                if (type == typeof(float))
                {
                    return value.GetSingle();
                }

                if (type == typeof(double))
                {
                    return value.GetDouble();
                }

                if (type == typeof(decimal))
                {
                    return value.GetDecimal();
                }

                if (type == typeof(Guid))
                {
                    return value.GetGuid();
                }

                if (type == typeof(DateOnly))
                {
                    return DateOnly.ParseExact(
                        RequiredStringValue(value, name),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                }

                if (type == typeof(TimeOnly))
                {
                    return TimeOnly.Parse(
                        RequiredStringValue(value, name),
                        CultureInfo.InvariantCulture);
                }

                if (type == typeof(DateTime))
                {
                    return value.GetDateTime();
                }

                if (type == typeof(DateTimeOffset))
                {
                    return value.GetDateTimeOffset();
                }
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidOperationException or OverflowException)
            {
                throw new LiveTransportRequestException(
                    $"Client-query parameter '{name}' is not a valid {type.Name} value.");
            }

            throw new LiveTransportRequestException(
                $"Client-query parameter '{name}' uses unsupported type '{type}'.");
        }

        private static Type ParseType(string type) =>
            type.ToLowerInvariant() switch
            {
                "string" => typeof(string),
                "boolean" or "bool" => typeof(bool),
                "byte" => typeof(byte),
                "sbyte" => typeof(sbyte),
                "int16" or "short" => typeof(short),
                "uint16" or "ushort" => typeof(ushort),
                "int32" or "int" => typeof(int),
                "uint32" or "uint" => typeof(uint),
                "int64" or "long" => typeof(long),
                "uint64" or "ulong" => typeof(ulong),
                "single" or "float" => typeof(float),
                "double" => typeof(double),
                "decimal" => typeof(decimal),
                "guid" => typeof(Guid),
                "date" => typeof(DateOnly),
                "time" => typeof(TimeOnly),
                "timestamp" => typeof(DateTime),
                "timestamptz" or "timestampoffset" => typeof(DateTimeOffset),
                _ => throw new LiveTransportRequestException(
                    $"Unsupported client-query parameter type '{type}'."),
            };

        private static string RequiredString(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var value))
            {
                throw new LiveTransportRequestException(
                    $"Required client-query property '{property}' is missing.");
            }

            return RequiredStringValue(value, property);
        }

        private static string RequiredStringValue(JsonElement value, string role)
        {
            if (value.ValueKind is not JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new LiveTransportRequestException(
                    $"Client-query {role} must be a non-empty string.");
            }

            return value.GetString()!;
        }

        private static int RequiredInt32(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var value) ||
                !value.TryGetInt32(out var result) ||
                result <= 0)
            {
                throw new LiveTransportRequestException(
                    $"Client-query property '{property}' must be a positive integer.");
            }

            return result;
        }

        private static string[] RequiredStringArray(
            JsonElement root,
            string property)
        {
            if (!root.TryGetProperty(property, out var values) ||
                values.ValueKind is not JsonValueKind.Array)
            {
                throw new LiveTransportRequestException(
                    $"Client-query property '{property}' must be an array.");
            }

            var result = values.EnumerateArray()
                .Select(value => RequiredStringValue(value, property))
                .ToArray();
            if (result.Length == 0)
            {
                throw new LiveTransportRequestException(
                    $"Client-query property '{property}' cannot be empty.");
            }

            return result;
        }

        private static void RequireOnly(
            JsonElement value,
            string role,
            string[] allowed)
        {
            if (value.ValueKind is not JsonValueKind.Object)
            {
                throw new LiveTransportRequestException(
                    $"Client-query {role} must be an object.");
            }

            foreach (var property in value.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new LiveTransportRequestException(
                        $"Unknown client-query {role} property '{property.Name}'.");
                }
            }
        }
    }
}
