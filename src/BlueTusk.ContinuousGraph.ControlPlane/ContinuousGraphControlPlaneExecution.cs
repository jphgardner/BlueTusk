using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BlueTusk.ContinuousGraph;
using BlueTusk.Live;

namespace BlueTusk.ControlPlane;

public sealed record ContinuousGraphControlPlaneExecutionOptions
{
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumConcurrentExecutions { get; init; } = 4;

    public int MaximumNodes { get; init; } = 10_000;

    public int MaximumEdges { get; init; } = 20_000;
}

public sealed class ContinuousGraphControlPlaneExecutionRegistry
{
    private readonly ConcurrentDictionary<string, IRegistration> _registrations =
        new(StringComparer.Ordinal);

    public int Count => _registrations.Count;

    public bool Register<TResult, TKey>(
        ContinuousGraphQueryPlan<TResult, TKey> plan,
        IReadOnlyDictionary<string, object?> boundArguments,
        IEnumerable<string> editableParameters,
        Func<ControlPlaneActor, LiveSecurityScope> securityScopeFactory,
        Func<TResult, ControlPlaneContinuousGraphFragment> projector)
        where TResult : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(boundArguments);
        ArgumentNullException.ThrowIfNull(editableParameters);
        ArgumentNullException.ThrowIfNull(securityScopeFactory);
        ArgumentNullException.ThrowIfNull(projector);
        var arguments = plan.Bind(boundArguments);
        var editable = editableParameters.Distinct(StringComparer.Ordinal).ToArray();
        if (editable.Any(name => !plan.LivePlan.Parameters.Any(parameter =>
                string.Equals(parameter.Name, name, StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                "Editable graph parameters must be declared by the compiled query plan.",
                nameof(editableParameters));
        }

        return _registrations.TryAdd(
            plan.Fingerprint,
            new Registration<TResult, TKey>(
                plan,
                arguments,
                editable,
                securityScopeFactory,
                projector));
    }

    public bool Unregister(string queryFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        return _registrations.TryRemove(queryFingerprint, out _);
    }

    public IReadOnlyList<ControlPlaneContinuousGraphParameterSnapshot> GetParameters(
        string queryFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        return _registrations.TryGetValue(queryFingerprint, out var registration)
            ? registration.Parameters
            : [];
    }

    public bool CanExecute(string queryFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        return _registrations.ContainsKey(queryFingerprint);
    }

    internal bool TryGet(string queryFingerprint, out IRegistration? registration) =>
        _registrations.TryGetValue(queryFingerprint, out registration);

    internal interface IRegistration
    {
        IReadOnlyList<ControlPlaneContinuousGraphParameterSnapshot> Parameters { get; }

        ValueTask<ControlPlaneContinuousGraphRunResult> ExecuteAsync(
            ControlPlaneActor actor,
            ControlPlaneContinuousGraphRunRequest request,
            ContinuousGraphControlPlaneExecutionOptions options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken);
    }

    private sealed class Registration<TResult, TKey> : IRegistration
        where TResult : class
        where TKey : notnull
    {
        private readonly ContinuousGraphQueryPlan<TResult, TKey> _plan;
        private readonly Dictionary<string, object?> _boundArguments;
        private readonly HashSet<string> _editableParameters;
        private readonly Func<ControlPlaneActor, LiveSecurityScope> _securityScopeFactory;
        private readonly Func<TResult, ControlPlaneContinuousGraphFragment> _projector;

        internal Registration(
            ContinuousGraphQueryPlan<TResult, TKey> plan,
            LiveQueryArguments boundArguments,
            IEnumerable<string> editableParameters,
            Func<ControlPlaneActor, LiveSecurityScope> securityScopeFactory,
            Func<TResult, ControlPlaneContinuousGraphFragment> projector)
        {
            _plan = plan;
            _boundArguments = new Dictionary<string, object?>(
                boundArguments.Values,
                StringComparer.Ordinal);
            _editableParameters = new HashSet<string>(editableParameters, StringComparer.Ordinal);
            _securityScopeFactory = securityScopeFactory;
            _projector = projector;
            Parameters = Array.AsReadOnly(plan.LivePlan.Parameters
                .Select(parameter => new ControlPlaneContinuousGraphParameterSnapshot(
                    parameter.Name,
                    FriendlyTypeName(parameter.ParameterType),
                    parameter.AllowNull,
                    _editableParameters.Contains(parameter.Name),
                    _editableParameters.Contains(parameter.Name)
                        ? FormatValue(_boundArguments[parameter.Name], parameter.ParameterType)
                        : null))
                .ToArray());
        }

        public IReadOnlyList<ControlPlaneContinuousGraphParameterSnapshot> Parameters { get; }

        public async ValueTask<ControlPlaneContinuousGraphRunResult> ExecuteAsync(
            ControlPlaneActor actor,
            ControlPlaneContinuousGraphRunRequest request,
            ContinuousGraphControlPlaneExecutionOptions options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(actor);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Parameters);
            var unknown = request.Parameters.Keys.FirstOrDefault(name =>
                !_editableParameters.Contains(name));
            if (unknown is not null)
            {
                throw new ControlPlaneContinuousGraphExecutionException(
                    "graph-parameter-not-editable",
                    $"Graph parameter '{unknown}' is not editable.");
            }

            var values = new Dictionary<string, object?>(_boundArguments, StringComparer.Ordinal);
            foreach (var parameter in _plan.LivePlan.Parameters)
            {
                if (!request.Parameters.TryGetValue(parameter.Name, out var value))
                {
                    continue;
                }

                try
                {
                    values[parameter.Name] = ParseValue(value, parameter);
                }
                catch (Exception exception) when (exception is FormatException or
                                                  OverflowException or
                                                  ArgumentException)
                {
                    throw new ControlPlaneContinuousGraphExecutionException(
                        "graph-parameter-invalid",
                        $"Graph parameter '{parameter.Name}' is invalid.");
                }
            }

            var arguments = _plan.Bind(values);
            var securityScope = _securityScopeFactory(actor) ??
                throw new ControlPlaneContinuousGraphExecutionException(
                    "graph-security-scope-unavailable",
                    "The graph security scope could not be resolved.");
            var started = Stopwatch.GetTimestamp();
            var rows = await _plan.LivePlan.ExecuteAsync(
                    new LiveQueryExecutionContext(arguments, securityScope),
                    cancellationToken)
                .ConfigureAwait(false);
            if (rows.Count > _plan.LivePlan.MaximumResultCount)
            {
                throw new ControlPlaneContinuousGraphExecutionException(
                    "graph-result-row-limit-exceeded",
                    "The authoritative graph result exceeded its registered row limit.");
            }

            var nodes = new Dictionary<string, ControlPlaneContinuousGraphNode>(StringComparer.Ordinal);
            var edges = new Dictionary<string, ControlPlaneContinuousGraphEdge>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var fragment = _projector(row) ??
                    throw InvalidProjection("A graph projector returned a null fragment.");
                AddNodes(nodes, fragment.Nodes, options.MaximumNodes);
                AddEdges(edges, fragment.Edges, options.MaximumEdges);
            }

            foreach (var edge in edges.Values)
            {
                if (!nodes.ContainsKey(edge.SourceNodeId) || !nodes.ContainsKey(edge.TargetNodeId))
                {
                    throw InvalidProjection(
                        $"Graph edge '{edge.Id}' references a node outside the projected result.");
                }
            }

            var orderedNodes = nodes.Values.OrderBy(static node => node.Category, StringComparer.Ordinal)
                .ThenBy(static node => node.Label, StringComparer.Ordinal)
                .ThenBy(static node => node.Id, StringComparer.Ordinal)
                .ToArray();
            var orderedEdges = edges.Values.OrderBy(static edge => edge.Category, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Label, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Id, StringComparer.Ordinal)
                .ToArray();
            return new ControlPlaneContinuousGraphRunResult(
                Guid.NewGuid(),
                timeProvider.GetUtcNow(),
                Stopwatch.GetElapsedTime(started),
                _plan.Fingerprint,
                _plan.Name,
                _plan.LivePlan.DatabaseIdentity,
                _plan.GraphName,
                _plan.GraphSchema,
                rows.Count,
                orderedNodes,
                orderedEdges,
                Compose(orderedNodes.Select(static node => node.Category)),
                Compose(orderedEdges.Select(static edge => edge.Category)));
        }

        private static void AddNodes(
            Dictionary<string, ControlPlaneContinuousGraphNode> target,
            IReadOnlyList<ControlPlaneContinuousGraphNode> nodes,
            int limit)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            foreach (var node in nodes)
            {
                ValidateNode(node);
                if (target.TryGetValue(node.Id, out var existing))
                {
                    if (!Equivalent(existing, node))
                    {
                        throw InvalidProjection($"Graph node '{node.Id}' has conflicting projections.");
                    }

                    continue;
                }

                if (target.Count == limit)
                {
                    throw LimitExceeded("nodes", limit);
                }

                target.Add(node.Id, node);
            }
        }

        private static void AddEdges(
            Dictionary<string, ControlPlaneContinuousGraphEdge> target,
            IReadOnlyList<ControlPlaneContinuousGraphEdge> edges,
            int limit)
        {
            ArgumentNullException.ThrowIfNull(edges);
            foreach (var edge in edges)
            {
                ValidateEdge(edge);
                if (target.TryGetValue(edge.Id, out var existing))
                {
                    if (!Equivalent(existing, edge))
                    {
                        throw InvalidProjection($"Graph edge '{edge.Id}' has conflicting projections.");
                    }

                    continue;
                }

                if (target.Count == limit)
                {
                    throw LimitExceeded("edges", limit);
                }

                target.Add(edge.Id, edge);
            }
        }

        private static void ValidateNode(ControlPlaneContinuousGraphNode node)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (string.IsNullOrWhiteSpace(node.Id) ||
                string.IsNullOrWhiteSpace(node.Label) ||
                string.IsNullOrWhiteSpace(node.Category) ||
                node.Properties is null)
            {
                throw InvalidProjection("A projected graph node is incomplete.");
            }

            ValidateProperties(node.Properties);
        }

        private static void ValidateEdge(ControlPlaneContinuousGraphEdge edge)
        {
            ArgumentNullException.ThrowIfNull(edge);
            if (string.IsNullOrWhiteSpace(edge.Id) ||
                string.IsNullOrWhiteSpace(edge.SourceNodeId) ||
                string.IsNullOrWhiteSpace(edge.TargetNodeId) ||
                string.IsNullOrWhiteSpace(edge.Label) ||
                string.IsNullOrWhiteSpace(edge.Category) ||
                edge.Properties is null)
            {
                throw InvalidProjection("A projected graph edge is incomplete.");
            }

            ValidateProperties(edge.Properties);
        }

        private static void ValidateProperties(
            IReadOnlyList<ControlPlaneContinuousGraphProperty> properties)
        {
            if (properties.Any(static property => property is null ||
                    string.IsNullOrWhiteSpace(property.Name)) ||
                properties.Select(static property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != properties.Count)
            {
                throw InvalidProjection("Projected graph property names must be non-empty and unique.");
            }
        }

        private static bool Equivalent(
            ControlPlaneContinuousGraphNode left,
            ControlPlaneContinuousGraphNode right) =>
            string.Equals(left.Label, right.Label, StringComparison.Ordinal) &&
            string.Equals(left.Category, right.Category, StringComparison.Ordinal) &&
            PropertiesEquivalent(left.Properties, right.Properties);

        private static bool Equivalent(
            ControlPlaneContinuousGraphEdge left,
            ControlPlaneContinuousGraphEdge right) =>
            string.Equals(left.SourceNodeId, right.SourceNodeId, StringComparison.Ordinal) &&
            string.Equals(left.TargetNodeId, right.TargetNodeId, StringComparison.Ordinal) &&
            string.Equals(left.Label, right.Label, StringComparison.Ordinal) &&
            string.Equals(left.Category, right.Category, StringComparison.Ordinal) &&
            left.Directed == right.Directed &&
            PropertiesEquivalent(left.Properties, right.Properties);

        private static bool PropertiesEquivalent(
            IReadOnlyList<ControlPlaneContinuousGraphProperty> left,
            IReadOnlyList<ControlPlaneContinuousGraphProperty> right) =>
            left.Count == right.Count && left.OrderBy(static property => property.Name, StringComparer.Ordinal)
                .SequenceEqual(right.OrderBy(static property => property.Name, StringComparer.Ordinal));

        private static ControlPlaneContinuousGraphComposition[] Compose(
            IEnumerable<string> categories) => categories
            .GroupBy(static category => category, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new ControlPlaneContinuousGraphComposition(group.Key, group.Count()))
            .ToArray();

        private static object? ParseValue(string? value, LiveQueryParameter parameter)
        {
            if (value is null)
            {
                if (!parameter.AllowNull)
                {
                    throw new ArgumentException("A non-null graph parameter cannot be null.");
                }

                return null;
            }

            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            if (type == typeof(string)) return value;
            if (type == typeof(bool)) return bool.Parse(value);
            if (type == typeof(byte)) return byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(sbyte)) return sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(short)) return short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(ushort)) return ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(uint)) return uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(ulong)) return ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
            if (type == typeof(Guid)) return Guid.Parse(value);
            if (type == typeof(DateOnly)) return DateOnly.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(TimeOnly)) return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: false);
            throw new ArgumentException("The graph parameter type is unsupported.");
        }

        private static string? FormatValue(object? value, Type type)
        {
            if (value is null) return null;
            if (value is IFormattable formattable)
            {
                return formattable.ToString(type == typeof(DateTime) || type == typeof(DateTimeOffset) ? "O" : null, CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        private static string FriendlyTypeName(Type type)
        {
            var valueType = Nullable.GetUnderlyingType(type) ?? type;
            return valueType.Name;
        }

        private static ControlPlaneContinuousGraphExecutionException InvalidProjection(string message) =>
            new("graph-result-invalid", message);

        private static ControlPlaneContinuousGraphExecutionException LimitExceeded(
            string element,
            int limit) =>
            new(
                "graph-visualization-limit-exceeded",
                $"The graph contains more than {limit.ToString(CultureInfo.InvariantCulture)} {element}; raise the explicit visualization limit to return it in full.");
    }
}

public sealed class HostedContinuousGraphControlPlaneExecutionService :
    IControlPlaneContinuousGraphExecutionService,
    IDisposable
{
    private readonly ContinuousGraphControlPlaneExecutionRegistry _registry;
    private readonly ContinuousGraphControlPlaneExecutionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _executions;

    public HostedContinuousGraphControlPlaneExecutionService(
        ContinuousGraphControlPlaneExecutionRegistry registry,
        ContinuousGraphControlPlaneExecutionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new ContinuousGraphControlPlaneExecutionOptions();
        Validate(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _executions = new SemaphoreSlim(
            _options.MaximumConcurrentExecutions,
            _options.MaximumConcurrentExecutions);
    }

    public async ValueTask<ControlPlaneContinuousGraphRunResult> ExecuteAsync(
        string queryFingerprint,
        ControlPlaneActor actor,
        ControlPlaneContinuousGraphRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!_registry.TryGet(queryFingerprint, out var registration) || registration is null)
        {
            throw new ControlPlaneContinuousGraphExecutionException(
                "graph-query-not-executable",
                "The registered graph query is not enabled for control-plane execution.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ExecutionTimeout);
        var entered = false;
        try
        {
            await _executions.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            return await registration.ExecuteAsync(
                    actor,
                    request,
                    _options,
                    _timeProvider,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ControlPlaneContinuousGraphExecutionException(
                "graph-execution-timeout",
                "The graph execution exceeded its configured timeout.");
        }
        finally
        {
            if (entered)
            {
                _executions.Release();
            }
        }
    }

    public void Dispose() => _executions.Dispose();

    private static void Validate(ContinuousGraphControlPlaneExecutionOptions options)
    {
        if (options.ExecutionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Graph execution timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConcurrentExecutions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumEdges);
    }
}

public sealed class ExecutableContinuousGraphControlPlaneQueryService :
    IControlPlaneContinuousGraphQueryService
{
    private readonly ContinuousGraphQueryRegistry _registry;
    private readonly ContinuousGraphControlPlaneExecutionRegistry _executionRegistry;
    private readonly TimeProvider _timeProvider;

    public ExecutableContinuousGraphControlPlaneQueryService(
        ContinuousGraphQueryRegistry registry,
        ContinuousGraphControlPlaneExecutionRegistry executionRegistry,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executionRegistry = executionRegistry ?? throw new ArgumentNullException(nameof(executionRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ControlPlaneContinuousGraphOverview> GetContinuousGraphOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queries = _registry.GetQueries()
            .Select(query => new ControlPlaneContinuousGraphQuerySnapshot(
                query.Name,
                query.DatabaseIdentity,
                query.Fingerprint,
                query.GraphName,
                query.GraphSchema,
                query.ElementTableAliases,
                query.Dependencies
                    .Select(static dependency => dependency.ToString())
                    .ToArray(),
                query.MaximumResultCount,
                query.Capabilities.ToString(),
                _executionRegistry.GetParameters(query.Fingerprint),
                _executionRegistry.CanExecute(query.Fingerprint)))
            .ToArray();
        return ValueTask.FromResult(
            new ControlPlaneContinuousGraphOverview(_timeProvider.GetUtcNow(), queries));
    }
}
