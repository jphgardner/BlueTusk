using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Live;

[Flags]
public enum LiveQueryCapabilities
{
    None = 0,
    SingleTable = 1 << 0,
    ParameterizedPredicate = 1 << 1,
    TenantFilter = 1 << 2,
    DeterministicOrdering = 1 << 3,
    BoundedTake = 1 << 4,
    OneToManyJoin = 1 << 5,
    Include = 1 << 6,
    Aggregate = 1 << 7,
    Grouping = 1 << 8,
    FullText = 1 << 9,
}

public sealed record LiveTableDependency
{
    public LiveTableDependency(string schema, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        Schema = schema;
        Table = table;
    }

    public string Schema { get; }

    public string Table { get; }

    public override string ToString() => $"{Schema}.{Table}";
}

public sealed record LiveQueryParameter
{
    public LiveQueryParameter(string name, Type parameterType, bool allowNull = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameterType);
        if (!IsSupportedParameterType(parameterType))
        {
            throw new ArgumentException(
                $"Live parameter type '{parameterType}' is not a supported scalar type.",
                nameof(parameterType));
        }

        Name = name;
        ParameterType = parameterType;
        AllowNull = allowNull;
    }

    public string Name { get; }

    public Type ParameterType { get; }

    public bool AllowNull { get; }

    private static bool IsSupportedParameterType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(decimal) ||
            type == typeof(Guid) ||
            type == typeof(DateOnly) ||
            type == typeof(TimeOnly) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset);
    }
}

public sealed class LiveQueryArguments
{
    private readonly ReadOnlyDictionary<string, object?> _values;

    private LiveQueryArguments(
        IReadOnlyList<LiveQueryParameter> parameters,
        IDictionary<string, object?> values,
        string fingerprint)
    {
        Parameters = parameters;
        _values = new ReadOnlyDictionary<string, object?>(values);
        Fingerprint = fingerprint;
    }

    public IReadOnlyList<LiveQueryParameter> Parameters { get; }

    public IReadOnlyDictionary<string, object?> Values => _values;

    public string Fingerprint { get; }

    public T? Get<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"Live query parameter '{name}' was not supplied.");
        }

        return value is null ? default : (T)value;
    }

    public static LiveQueryArguments Create(
        IEnumerable<LiveQueryParameter> parameters,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(values);
        var definitions = parameters.ToArray();
        if (definitions.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
        {
            throw new ArgumentException("Live query parameter names must be unique.", nameof(parameters));
        }

        if (values.Count != definitions.Length ||
            values.Keys.Any(name => !definitions.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                "Live query arguments must exactly match the registered parameter names.",
                nameof(values));
        }

        var materialized = new Dictionary<string, object?>(definitions.Length, StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var definition in definitions)
        {
            if (!values.TryGetValue(definition.Name, out var value))
            {
                throw new ArgumentException(
                    $"Required live query parameter '{definition.Name}' was not supplied.",
                    nameof(values));
            }

            if (value is null && !definition.AllowNull)
            {
                throw new ArgumentException(
                    $"Live query parameter '{definition.Name}' cannot be null.",
                    nameof(values));
            }

            var targetType = Nullable.GetUnderlyingType(definition.ParameterType) ?? definition.ParameterType;
            if (value is not null && !targetType.IsInstanceOfType(value))
            {
                throw new ArgumentException(
                    $"Live query parameter '{definition.Name}' requires '{targetType}', not '{value.GetType()}'.",
                    nameof(values));
            }

            materialized.Add(definition.Name, value);
            Append(hash, definition.Name);
            Append(hash, targetType.FullName ?? targetType.Name);
            if (value is null)
            {
                hash.AppendData([0]);
            }
            else
            {
                hash.AppendData([1]);
                var encoded = JsonSerializer.SerializeToUtf8Bytes(value, targetType);
                Append(hash, encoded);
            }
        }

        return new LiveQueryArguments(
            Array.AsReadOnly(definitions),
            materialized,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

public sealed record LiveSecurityScope
{
    public LiveSecurityScope(string scope, string authorizationPolicyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicyVersion);
        Scope = scope;
        AuthorizationPolicyVersion = authorizationPolicyVersion;
    }

    public string Scope { get; }

    public string AuthorizationPolicyVersion { get; }
}

public sealed record LiveQueryExecutionContext(
    LiveQueryArguments Arguments,
    LiveSecurityScope SecurityScope);

public sealed class LiveQueryPlan<T, TKey>
    where TKey : notnull
{
    private readonly ReadOnlyCollection<LiveTableDependency> _dependencies;
    private readonly ReadOnlyCollection<LiveQueryParameter> _parameters;

    public LiveQueryPlan(
        string name,
        string databaseIdentity,
        string fingerprint,
        LiveQueryCapabilities capabilities,
        IEnumerable<LiveTableDependency> dependencies,
        IEnumerable<LiveQueryParameter> parameters,
        int maximumResultCount,
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<IReadOnlyList<T>>> executeAsync,
        Func<T, TKey> keySelector,
        IEqualityComparer<T>? rowComparer = null,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ValidateFingerprint(fingerprint);
        const LiveQueryCapabilities allCapabilities =
            LiveQueryCapabilities.SingleTable |
            LiveQueryCapabilities.ParameterizedPredicate |
            LiveQueryCapabilities.TenantFilter |
            LiveQueryCapabilities.DeterministicOrdering |
            LiveQueryCapabilities.BoundedTake |
            LiveQueryCapabilities.OneToManyJoin |
            LiveQueryCapabilities.Include |
            LiveQueryCapabilities.Aggregate |
            LiveQueryCapabilities.Grouping |
            LiveQueryCapabilities.FullText;
        if ((capabilities & ~allCapabilities) != LiveQueryCapabilities.None)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        }

        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultCount);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(keySelector);
        var dependencyArray = dependencies.Distinct().ToArray();
        if (dependencyArray.Length == 0)
        {
            throw new ArgumentException("A live query must declare at least one table dependency.", nameof(dependencies));
        }

        var parameterArray = parameters.ToArray();
        _ = LiveQueryArguments.Create(
            parameterArray,
            parameterArray.ToDictionary(
                parameter => parameter.Name,
                parameter => DefaultValue(parameter),
                StringComparer.Ordinal));

        Name = name;
        DatabaseIdentity = databaseIdentity;
        Fingerprint = fingerprint.ToLowerInvariant();
        Capabilities = capabilities;
        _dependencies = Array.AsReadOnly(dependencyArray);
        _parameters = Array.AsReadOnly(parameterArray);
        MaximumResultCount = maximumResultCount;
        ExecuteAsync = executeAsync;
        KeySelector = keySelector;
        RowComparer = rowComparer ?? EqualityComparer<T>.Default;
        KeyComparer = keyComparer ?? EqualityComparer<TKey>.Default;
    }

    public string Name { get; }

    public string DatabaseIdentity { get; }

    public string Fingerprint { get; }

    public LiveQueryCapabilities Capabilities { get; }

    public IReadOnlyList<LiveTableDependency> Dependencies => _dependencies;

    public IReadOnlyList<LiveQueryParameter> Parameters => _parameters;

    public int MaximumResultCount { get; }

    public Func<LiveQueryExecutionContext, CancellationToken, ValueTask<IReadOnlyList<T>>> ExecuteAsync { get; }

    public Func<T, TKey> KeySelector { get; }

    public IEqualityComparer<T> RowComparer { get; }

    public IEqualityComparer<TKey> KeyComparer { get; }

    public LiveQueryArguments Bind(IReadOnlyDictionary<string, object?> values) =>
        LiveQueryArguments.Create(_parameters, values);

    private static void ValidateFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A live query fingerprint must be a 64-character SHA-256 hexadecimal value.",
                nameof(fingerprint));
        }
    }

    private static object? DefaultValue(LiveQueryParameter parameter)
    {
        if (parameter.AllowNull)
        {
            return null;
        }

        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (type == typeof(string))
        {
            return string.Empty;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

}

public static class LiveQueryFingerprint
{
    public static string Create(
        string name,
        string version,
        ReadOnlySpan<byte> canonicalPlan = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, name);
        Append(hash, version);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, canonicalPlan.Length);
        hash.AppendData(length);
        hash.AppendData(canonicalPlan);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record LiveSubscriptionIdentity
{
    public LiveSubscriptionIdentity(
        string databaseIdentity,
        string queryPlanFingerprint,
        string parameterFingerprint,
        string securityScope,
        string authorizationPolicyVersion,
        int resultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ValidateFingerprint(queryPlanFingerprint, nameof(queryPlanFingerprint));
        ValidateFingerprint(parameterFingerprint, nameof(parameterFingerprint));
        ArgumentException.ThrowIfNullOrWhiteSpace(securityScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicyVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultLimit);
        DatabaseIdentity = databaseIdentity;
        QueryPlanFingerprint = queryPlanFingerprint.ToLowerInvariant();
        ParameterFingerprint = parameterFingerprint.ToLowerInvariant();
        SecurityScope = securityScope;
        AuthorizationPolicyVersion = authorizationPolicyVersion;
        ResultLimit = resultLimit;
        Fingerprint = ComputeFingerprint(this);
    }

    public string DatabaseIdentity { get; }

    public string QueryPlanFingerprint { get; }

    public string ParameterFingerprint { get; }

    public string SecurityScope { get; }

    public string AuthorizationPolicyVersion { get; }

    public int ResultLimit { get; }

    public string Fingerprint { get; }

    public static LiveSubscriptionIdentity Create<T, TKey>(
        LiveQueryPlan<T, TKey> plan,
        LiveQueryArguments arguments,
        LiveSecurityScope securityScope,
        int? resultLimit = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(securityScope);
        var limit = resultLimit ?? plan.MaximumResultCount;
        if (limit <= 0 || limit > plan.MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultLimit),
                FormattableString.Invariant($"The live result limit must be between 1 and {plan.MaximumResultCount}."));
        }

        return new LiveSubscriptionIdentity(
            plan.DatabaseIdentity,
            plan.Fingerprint,
            arguments.Fingerprint,
            securityScope.Scope,
            securityScope.AuthorizationPolicyVersion,
            limit);
    }

    private static string ComputeFingerprint(LiveSubscriptionIdentity identity)
    {
        var canonical = string.Join(
            '\n',
            identity.DatabaseIdentity,
            identity.QueryPlanFingerprint,
            identity.ParameterFingerprint,
            identity.SecurityScope,
            identity.AuthorizationPolicyVersion,
            identity.ResultLimit.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateFingerprint(string fingerprint, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint, parameterName);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A fingerprint must contain 64 hexadecimal characters.", parameterName);
        }
    }
}

public sealed class LiveQueryRegistry
{
    private readonly ConcurrentDictionary<string, object> _plans = new(StringComparer.Ordinal);

    public int Count => _plans.Count;

    public void Register<T, TKey>(LiveQueryPlan<T, TKey> plan)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_plans.TryAdd(plan.Name, plan))
        {
            throw new InvalidOperationException($"Live query '{plan.Name}' is already registered.");
        }
    }

    public LiveQueryPlan<T, TKey> Get<T, TKey>(string name)
        where TKey : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_plans.TryGetValue(name, out var plan))
        {
            throw new KeyNotFoundException($"Live query '{name}' is not registered.");
        }

        return plan as LiveQueryPlan<T, TKey> ??
            throw new InvalidOperationException(
                $"Live query '{name}' is registered for a different row or key type.");
    }
}
