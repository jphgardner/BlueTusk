using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.Live.EntityFrameworkCore;

public sealed class LiveEfProjectionQueryDefinition<TContext, TRoot, TResult, TKey>
    where TContext : DbContext
    where TRoot : class
    where TResult : class
    where TKey : notnull
{
    private readonly ReadOnlyCollection<LiveQueryParameter> _parameters;

    public LiveEfProjectionQueryDefinition(
        string name,
        string databaseIdentity,
        string version,
        IEnumerable<LiveQueryParameter> parameters,
        IReadOnlyDictionary<string, object?> validationArguments,
        int maximumResultCount,
        Func<TContext, LiveQueryArguments, IQueryable<TResult>> queryFactory,
        Expression<Func<TResult, TKey>> keySelector,
        IEqualityComparer<TResult> rowComparer,
        LiveEfTenantIsolationMode tenantIsolationMode,
        LiveEfTenantBinding? tenantBinding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(validationArguments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultCount);
        ArgumentNullException.ThrowIfNull(queryFactory);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(rowComparer);
        if (!Enum.IsDefined(tenantIsolationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(tenantIsolationMode));
        }

        if (tenantIsolationMode is LiveEfTenantIsolationMode.RegisteredPredicate && tenantBinding is null)
        {
            throw new ArgumentException(
                "Registered-predicate tenant isolation requires a root-property/parameter binding.",
                nameof(tenantBinding));
        }

        if (tenantIsolationMode is not LiveEfTenantIsolationMode.RegisteredPredicate && tenantBinding is not null)
        {
            throw new ArgumentException(
                "A tenant binding is valid only for registered-predicate isolation.",
                nameof(tenantBinding));
        }

        var parameterArray = parameters.ToArray();
        Name = name;
        DatabaseIdentity = databaseIdentity;
        Version = version;
        _parameters = Array.AsReadOnly(parameterArray);
        ValidationArguments = LiveQueryArguments.Create(parameterArray, validationArguments);
        MaximumResultCount = maximumResultCount;
        QueryFactory = queryFactory;
        KeySelector = keySelector;
        RowComparer = rowComparer;
        TenantIsolationMode = tenantIsolationMode;
        TenantBinding = tenantBinding;
    }

    public string Name { get; }

    public string DatabaseIdentity { get; }

    public string Version { get; }

    public IReadOnlyList<LiveQueryParameter> Parameters => _parameters;

    public LiveQueryArguments ValidationArguments { get; }

    public int MaximumResultCount { get; }

    public Func<TContext, LiveQueryArguments, IQueryable<TResult>> QueryFactory { get; }

    public Expression<Func<TResult, TKey>> KeySelector { get; }

    public IEqualityComparer<TResult> RowComparer { get; }

    public LiveEfTenantIsolationMode TenantIsolationMode { get; }

    public LiveEfTenantBinding? TenantBinding { get; }
}

public static partial class LiveEfQueryCompiler
{
    public static async ValueTask<LiveQueryPlan<TResult, TKey>>
        CompileProjectionAsync<TContext, TRoot, TResult, TKey>(
            IDbContextFactory<TContext> contextFactory,
            LiveEfProjectionQueryDefinition<TContext, TRoot, TResult, TKey> definition,
            CancellationToken cancellationToken = default)
        where TContext : DbContext
        where TRoot : class
        where TResult : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(definition);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rootEntityType = context.Model.FindEntityType(typeof(TRoot)) ??
            throw new LiveEfQueryRegistrationException(
                $"Root entity '{typeof(TRoot)}' is not part of the supplied EF model.");
        if (rootEntityType.GetTableName() is null)
        {
            throw new LiveEfQueryRegistrationException(
                $"Root entity '{rootEntityType.DisplayName()}' is not mapped to a table.");
        }

        var resultKeyProperty = RequireMemberName(definition.KeySelector.Body, "result key selector");
        var query = definition.QueryFactory(context, definition.ValidationArguments) ??
            throw new LiveEfQueryRegistrationException("The registered EF projection factory returned null.");
        var dependencies = EntityDependencyCollector.Collect(
            context.Model,
            rootEntityType,
            query.Expression);
        var shape = ProjectedLiveEfQueryShape.Validate(
            query.Expression,
            context.Model,
            rootEntityType,
            dependencies,
            definition,
            resultKeyProperty);
        try
        {
            _ = query.ToQueryString();
        }
        catch (Exception exception)
        {
            throw new LiveEfQueryRegistrationException(
                $"Live projection '{definition.Name}' could not be translated by EF at registration time.",
                exception);
        }

        var canonicalPlan = string.Join(
            '\n',
            typeof(TContext).AssemblyQualifiedName,
            typeof(TRoot).AssemblyQualifiedName,
            typeof(TResult).AssemblyQualifiedName,
            typeof(TKey).AssemblyQualifiedName,
            resultKeyProperty,
            definition.TenantIsolationMode.ToString(),
            definition.TenantBinding?.EntityProperty,
            definition.TenantBinding?.ParameterName,
            definition.MaximumResultCount.ToString(CultureInfo.InvariantCulture),
            shape.CanonicalExpression,
            string.Join(',', dependencies.Select(static dependency => dependency.ToString())),
            definition.RowComparer.GetType().AssemblyQualifiedName);
        var fingerprint = LiveQueryFingerprint.Create(
            definition.Name,
            definition.Version,
            Encoding.UTF8.GetBytes(canonicalPlan));
        return new LiveQueryPlan<TResult, TKey>(
            definition.Name,
            definition.DatabaseIdentity,
            fingerprint,
            shape.Capabilities,
            dependencies,
            definition.Parameters,
            definition.MaximumResultCount,
            async (execution, token) =>
            {
                await using var executionContext =
                    await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                var executionQuery = definition.QueryFactory(executionContext, execution.Arguments) ??
                    throw new LiveEfQueryRegistrationException(
                        $"Live projection '{definition.Name}' returned null during execution.");
                return await executionQuery.AsNoTracking().ToListAsync(token).ConfigureAwait(false);
            },
            definition.KeySelector.Compile(),
            definition.RowComparer);
    }

    private sealed record ProjectionQueryShape(
        LiveQueryCapabilities Capabilities,
        string CanonicalExpression);

    private sealed class ProjectedLiveEfQueryShape : ExpressionVisitor
    {
        private static readonly HashSet<string> AggregateMethods =
        [
            nameof(Enumerable.Count),
            nameof(Enumerable.LongCount),
            nameof(Enumerable.Sum),
            nameof(Enumerable.Min),
            nameof(Enumerable.Max),
            nameof(Enumerable.Average),
        ];

        private readonly IModel _model;
        private readonly IEntityType _rootEntityType;
        private readonly int _maximumResultCount;
        private readonly List<LambdaExpression> _predicates = [];
        private readonly HashSet<string> _orderProperties = new(StringComparer.Ordinal);
        private bool _hasPredicate;
        private bool _hasTake;
        private bool _hasOneToManyJoin;
        private bool _hasGrouping;
        private bool _hasAggregate;
        private bool _hasFullText;

        private ProjectedLiveEfQueryShape(
            IModel model,
            IEntityType rootEntityType,
            int maximumResultCount)
        {
            _model = model;
            _rootEntityType = rootEntityType;
            _maximumResultCount = maximumResultCount;
        }

        public static ProjectionQueryShape Validate<TContext, TRoot, TResult, TKey>(
            Expression expression,
            IModel model,
            IEntityType rootEntityType,
            LiveTableDependency[] dependencies,
            LiveEfProjectionQueryDefinition<TContext, TRoot, TResult, TKey> definition,
            string resultKeyProperty)
            where TContext : DbContext
            where TRoot : class
            where TResult : class
            where TKey : notnull
        {
            var validator = new ProjectedLiveEfQueryShape(
                model,
                rootEntityType,
                definition.MaximumResultCount);
            validator.Visit(expression);
            if (!validator._hasTake)
            {
                throw new LiveEfQueryRegistrationException(
                    "A projected Live query must contain one bounded Take operation.");
            }

            if (!validator._orderProperties.Contains(resultKeyProperty))
            {
                throw new LiveEfQueryRegistrationException(
                    $"A projected Live query must have deterministic ordering that includes result key '{resultKeyProperty}'.");
            }

            ValidateProjectionTenantIsolation(
                rootEntityType,
                definition,
                validator._hasPredicate,
                validator._predicates);
            var capabilities = LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake;
            if (dependencies.Length == 1)
            {
                capabilities |= LiveQueryCapabilities.SingleTable;
            }

            if (validator._hasPredicate)
            {
                capabilities |= LiveQueryCapabilities.ParameterizedPredicate;
            }

            if (validator._hasOneToManyJoin)
            {
                capabilities |= LiveQueryCapabilities.OneToManyJoin;
            }

            if (validator._hasGrouping)
            {
                capabilities |= LiveQueryCapabilities.Grouping;
            }

            if (validator._hasAggregate)
            {
                capabilities |= LiveQueryCapabilities.Aggregate;
            }

            if (validator._hasFullText)
            {
                capabilities |= LiveQueryCapabilities.FullText;
            }

            return new ProjectionQueryShape(capabilities, expression.ToString());
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable))
            {
                VisitQueryableMethod(node);
                return base.VisitMethodCall(node);
            }

            if (node.Method.DeclaringType == typeof(Enumerable) &&
                AggregateMethods.Contains(node.Method.Name))
            {
                _hasAggregate = true;
                return base.VisitMethodCall(node);
            }

            if (node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) &&
                node.Method.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking))
            {
                return base.VisitMethodCall(node);
            }

            if (node.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions) &&
                (node.Method.Name.Contains("FullText", StringComparison.Ordinal) ||
                 node.Method.Name.Contains("TextSearch", StringComparison.Ordinal)))
            {
                _hasFullText = true;
                return base.VisitMethodCall(node);
            }

            if ((node.Method.DeclaringType == typeof(string) &&
                 node.Method.Name is nameof(string.Contains) or nameof(string.StartsWith) or nameof(string.EndsWith)) ||
                (node.Method.DeclaringType == typeof(EF) && node.Method.Name == nameof(EF.Property)))
            {
                return base.VisitMethodCall(node);
            }

            throw new LiveEfQueryRegistrationException(
                $"Method '{node.Method.DeclaringType?.Name}.{node.Method.Name}' is not supported by the projected Live compiler.");
        }

        protected override Expression VisitInvocation(InvocationExpression node) =>
            throw new LiveEfQueryRegistrationException(
                "Invoked expression trees are not supported by the projected Live compiler.");

        private void VisitQueryableMethod(MethodCallExpression node)
        {
            switch (node.Method.Name)
            {
                case nameof(Queryable.Where):
                    _hasPredicate = true;
                    var predicate = RequireLambda(node.Arguments[1], "predicate");
                    _hasFullText |= SimplePredicateValidator.Validate(predicate.Body);
                    _predicates.Add(predicate);
                    break;
                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                    _orderProperties.Add(RequireMemberName(
                        RequireLambda(node.Arguments[1], "ordering").Body,
                        "ordering selector"));
                    break;
                case nameof(Queryable.Take):
                    if (_hasTake)
                    {
                        throw new LiveEfQueryRegistrationException(
                            "A projected Live query must contain exactly one bounded Take operation.");
                    }

                    _hasTake = true;
                    var take = EvaluateProjectionTake(node.Arguments[1]);
                    if (take <= 0 || take > _maximumResultCount)
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Projected Live query Take({take}) must be between 1 and {_maximumResultCount}.");
                    }

                    break;
                case nameof(Queryable.Select):
                    break;
                case nameof(Queryable.SelectMany):
                    ValidateOneToManyNavigation(node);
                    _hasOneToManyJoin = true;
                    break;
                case nameof(Queryable.GroupBy):
                    _hasGrouping = true;
                    break;
                case nameof(Queryable.Join):
                case nameof(Queryable.GroupJoin):
                    throw new LiveEfQueryRegistrationException(
                        "Projected Live joins must use a mapped collection navigation with SelectMany so one-to-many cardinality can be proven.");
                default:
                    if (AggregateMethods.Contains(node.Method.Name))
                    {
                        _hasAggregate = true;
                        break;
                    }

                    throw new LiveEfQueryRegistrationException(
                        $"Queryable method '{node.Method.Name}' is not supported by the projected Live compiler.");
            }
        }

        private void ValidateOneToManyNavigation(MethodCallExpression node)
        {
            if (node.Arguments.Count < 2)
            {
                throw new LiveEfQueryRegistrationException(
                    "Projected Live SelectMany requires a mapped collection navigation.");
            }

            var collectionSelector = RequireLambda(node.Arguments[1], "collection selector");
            var body = StripConvert(collectionSelector.Body);
            if (collectionSelector.Parameters.Count != 1 ||
                collectionSelector.Parameters[0].Type != _rootEntityType.ClrType ||
                body is not MemberExpression member ||
                member.Expression != collectionSelector.Parameters[0])
            {
                throw new LiveEfQueryRegistrationException(
                    "Projected Live SelectMany must directly select a collection navigation from the registered root entity.");
            }

            var navigation = _rootEntityType.FindNavigation(member.Member.Name);
            if (navigation is null || !navigation.IsCollection || navigation.ForeignKey.IsUnique)
            {
                throw new LiveEfQueryRegistrationException(
                    $"Navigation '{member.Member.Name}' is not a mapped one-to-many collection.");
            }

            if (_model.FindEntityType(navigation.TargetEntityType.ClrType)?.GetTableName() is null)
            {
                throw new LiveEfQueryRegistrationException(
                    $"Navigation target '{navigation.TargetEntityType.DisplayName()}' is not mapped to a table.");
            }
        }

        private static void ValidateProjectionTenantIsolation<TContext, TRoot, TResult, TKey>(
            IEntityType rootEntityType,
            LiveEfProjectionQueryDefinition<TContext, TRoot, TResult, TKey> definition,
            bool hasPredicate,
            IReadOnlyList<LambdaExpression> predicates)
            where TContext : DbContext
            where TRoot : class
            where TResult : class
            where TKey : notnull
        {
            switch (definition.TenantIsolationMode)
            {
                case LiveEfTenantIsolationMode.DatabaseRowLevelSecurity:
                    return;
                case LiveEfTenantIsolationMode.EfGlobalQueryFilter:
                    if (rootEntityType.GetDeclaredQueryFilters().Count == 0)
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Root entity '{rootEntityType.DisplayName()}' does not have the required EF global query filter.");
                    }

                    return;
                case LiveEfTenantIsolationMode.RegisteredPredicate:
                    var binding = definition.TenantBinding!;
                    if (!hasPredicate || rootEntityType.FindProperty(binding.EntityProperty) is null)
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Registered tenant property '{binding.EntityProperty}' is absent from the root entity or query predicate.");
                    }

                    if (!definition.ValidationArguments.Values.TryGetValue(binding.ParameterName, out var expectedValue))
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Registered tenant parameter '{binding.ParameterName}' is not declared by the Live projection.");
                    }

                    if (!predicates.Any(predicate =>
                            predicate.Parameters.Count == 1 &&
                            predicate.Parameters[0].Type == typeof(TRoot) &&
                            TenantPredicateMatcher.Matches(
                                predicate,
                                binding.EntityProperty,
                                expectedValue)))
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Live projection does not bind root property '{binding.EntityProperty}' to parameter '{binding.ParameterName}' before projection.");
                    }

                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(definition));
            }
        }

        private static LambdaExpression RequireLambda(Expression expression, string role)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            {
                expression = quote.Operand;
            }

            return expression as LambdaExpression ??
                throw new LiveEfQueryRegistrationException(
                    $"The projected Live query {role} is not a lambda expression.");
        }

        private static int EvaluateProjectionTake(Expression expression)
        {
            try
            {
                return Expression.Lambda<Func<int>>(Expression.Convert(expression, typeof(int)))
                    .Compile()
                    .Invoke();
            }
            catch (Exception exception)
            {
                throw new LiveEfQueryRegistrationException(
                    "The projected Live query Take bound could not be evaluated during registration.",
                    exception);
            }
        }
    }
}
