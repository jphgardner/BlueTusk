using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.Live.EntityFrameworkCore;

public enum LiveEfTenantIsolationMode
{
    DatabaseRowLevelSecurity,
    EfGlobalQueryFilter,
    RegisteredPredicate,
}

public sealed record LiveEfTenantBinding
{
    public LiveEfTenantBinding(string entityProperty, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityProperty);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        EntityProperty = entityProperty;
        ParameterName = parameterName;
    }

    public string EntityProperty { get; }

    public string ParameterName { get; }
}

public sealed class LiveEfQueryDefinition<TContext, TEntity, TKey>
    where TContext : DbContext
    where TEntity : class
    where TKey : notnull
{
    private readonly ReadOnlyCollection<LiveQueryParameter> _parameters;

    public LiveEfQueryDefinition(
        string name,
        string databaseIdentity,
        string version,
        IEnumerable<LiveQueryParameter> parameters,
        IReadOnlyDictionary<string, object?> validationArguments,
        int maximumResultCount,
        Func<TContext, LiveQueryArguments, IQueryable<TEntity>> queryFactory,
        Expression<Func<TEntity, TKey>> keySelector,
        IEqualityComparer<TEntity> rowComparer,
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
                "Registered-predicate tenant isolation requires an entity-property/parameter binding.",
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

    public Func<TContext, LiveQueryArguments, IQueryable<TEntity>> QueryFactory { get; }

    public Expression<Func<TEntity, TKey>> KeySelector { get; }

    public IEqualityComparer<TEntity> RowComparer { get; }

    public LiveEfTenantIsolationMode TenantIsolationMode { get; }

    public LiveEfTenantBinding? TenantBinding { get; }
}

public static class LiveEfQueryCompiler
{
    public static async ValueTask<LiveQueryPlan<TEntity, TKey>> CompileAsync<TContext, TEntity, TKey>(
        IDbContextFactory<TContext> contextFactory,
        LiveEfQueryDefinition<TContext, TEntity, TKey> definition,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(definition);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entityType = context.Model.FindEntityType(typeof(TEntity)) ??
            throw new LiveEfQueryRegistrationException(
                $"Entity '{typeof(TEntity)}' is not part of the supplied EF model.");
        var tableName = entityType.GetTableName() ??
            throw new LiveEfQueryRegistrationException(
                $"Entity '{entityType.DisplayName()}' is not mapped to a table.");
        var schema = entityType.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public";
        var key = entityType.FindPrimaryKey() ??
            throw new LiveEfQueryRegistrationException(
                $"Entity '{entityType.DisplayName()}' must have a primary key for Live registration.");
        if (key.Properties.Count != 1)
        {
            throw new LiveEfQueryRegistrationException(
                $"Preview Live registration requires one primary-key property; '{entityType.DisplayName()}' has {key.Properties.Count}.");
        }

        var keyPropertyName = RequireMemberName(definition.KeySelector.Body, "key selector");
        if (!string.Equals(key.Properties[0].Name, keyPropertyName, StringComparison.Ordinal))
        {
            throw new LiveEfQueryRegistrationException(
                $"Live key selector '{keyPropertyName}' must match EF primary key '{key.Properties[0].Name}'.");
        }

        var query = definition.QueryFactory(context, definition.ValidationArguments) ??
            throw new LiveEfQueryRegistrationException("The registered EF query factory returned null.");
        var shape = LiveEfQueryShape.Validate(
            query.Expression,
            entityType,
            definition,
            keyPropertyName);
        try
        {
            _ = query.ToQueryString();
        }
        catch (Exception exception)
        {
            throw new LiveEfQueryRegistrationException(
                $"Live query '{definition.Name}' could not be translated by EF at registration time.",
                exception);
        }

        var canonicalPlan = string.Join(
            '\n',
            typeof(TContext).AssemblyQualifiedName,
            typeof(TEntity).AssemblyQualifiedName,
            typeof(TKey).AssemblyQualifiedName,
            schema,
            tableName,
            keyPropertyName,
            definition.TenantIsolationMode.ToString(),
            definition.TenantBinding?.EntityProperty,
            definition.TenantBinding?.ParameterName,
            definition.MaximumResultCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            shape.CanonicalExpression,
            definition.RowComparer.GetType().AssemblyQualifiedName);
        var fingerprint = LiveQueryFingerprint.Create(
            definition.Name,
            definition.Version,
            Encoding.UTF8.GetBytes(canonicalPlan));
        return new LiveQueryPlan<TEntity, TKey>(
            definition.Name,
            definition.DatabaseIdentity,
            fingerprint,
            shape.Capabilities,
            [new LiveTableDependency(schema, tableName)],
            definition.Parameters,
            definition.MaximumResultCount,
            async (execution, token) =>
            {
                await using var executionContext = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                var executionQuery = definition.QueryFactory(executionContext, execution.Arguments) ??
                    throw new LiveEfQueryRegistrationException(
                        $"Live query '{definition.Name}' returned null during execution.");
                return await executionQuery.AsNoTracking().ToListAsync(token).ConfigureAwait(false);
            },
            definition.KeySelector.Compile(),
            definition.RowComparer);
    }

    private static string RequireMemberName(Expression expression, string role)
    {
        expression = StripConvert(expression);
        return expression is MemberExpression { Expression: ParameterExpression } member
            ? member.Member.Name
            : throw new LiveEfQueryRegistrationException(
                $"The Live {role} must be a direct entity property access.");
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private sealed record QueryShape(LiveQueryCapabilities Capabilities, string CanonicalExpression);

    private static class LiveEfQueryShape
    {
        public static QueryShape Validate<TContext, TEntity, TKey>(
            Expression expression,
            IEntityType entityType,
            LiveEfQueryDefinition<TContext, TEntity, TKey> definition,
            string keyPropertyName)
            where TContext : DbContext
            where TEntity : class
            where TKey : notnull
        {
            var hasPredicate = false;
            var hasTake = false;
            var orderProperties = new List<string>();
            var predicates = new List<LambdaExpression>();
            var current = expression;
            while (current is MethodCallExpression methodCall && IsQueryableChainMethod(methodCall))
            {
                var methodName = methodCall.Method.Name;
                if (methodCall.Method.DeclaringType == typeof(Queryable))
                {
                    switch (methodName)
                    {
                        case nameof(Queryable.Where):
                            hasPredicate = true;
                            var predicate = RequireLambda(methodCall.Arguments[1], "predicate");
                            SimplePredicateValidator.Validate(predicate.Body);
                            predicates.Add(predicate);
                            break;
                        case nameof(Queryable.OrderBy):
                        case nameof(Queryable.OrderByDescending):
                        case nameof(Queryable.ThenBy):
                        case nameof(Queryable.ThenByDescending):
                            orderProperties.Add(RequireMemberName(
                                RequireLambda(methodCall.Arguments[1], "ordering").Body,
                                "ordering selector"));
                            break;
                        case nameof(Queryable.Take):
                            if (hasTake)
                            {
                                throw new LiveEfQueryRegistrationException(
                                    "A Live query must contain exactly one bounded Take operation.");
                            }

                            hasTake = true;
                            var take = EvaluateTake(methodCall.Arguments[1]);
                            if (take <= 0 || take > definition.MaximumResultCount)
                            {
                                throw new LiveEfQueryRegistrationException(
                                    $"Live query Take({take}) must be between 1 and {definition.MaximumResultCount}.");
                            }

                            break;
                        default:
                            throw new LiveEfQueryRegistrationException(
                                $"Queryable method '{methodName}' is not supported by the initial Live compiler.");
                    }
                }
                else if (methodCall.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions) ||
                    methodName != nameof(EntityFrameworkQueryableExtensions.AsNoTracking))
                {
                    throw new LiveEfQueryRegistrationException(
                        $"Query extension '{methodCall.Method.DeclaringType?.Name}.{methodName}' is not supported by the initial Live compiler.");
                }

                current = methodCall.Arguments[0];
            }

            if (!hasTake)
            {
                throw new LiveEfQueryRegistrationException("A Live query must contain one bounded Take operation.");
            }

            if (orderProperties.Count == 0 || !orderProperties.Contains(keyPropertyName, StringComparer.Ordinal))
            {
                throw new LiveEfQueryRegistrationException(
                    $"A Live query must have deterministic ordering that includes primary key '{keyPropertyName}'.");
            }

            ValidateTenantIsolation(entityType, definition, hasPredicate, predicates);
            var capabilities = LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake |
                LiveQueryCapabilities.TenantFilter;
            if (hasPredicate)
            {
                capabilities |= LiveQueryCapabilities.ParameterizedPredicate;
            }

            return new QueryShape(capabilities, expression.ToString());
        }

        private static void ValidateTenantIsolation<TContext, TEntity, TKey>(
            IEntityType entityType,
            LiveEfQueryDefinition<TContext, TEntity, TKey> definition,
            bool hasPredicate,
            IReadOnlyList<LambdaExpression> predicates)
            where TContext : DbContext
            where TEntity : class
            where TKey : notnull
        {
            switch (definition.TenantIsolationMode)
            {
                case LiveEfTenantIsolationMode.DatabaseRowLevelSecurity:
                    return;
                case LiveEfTenantIsolationMode.EfGlobalQueryFilter:
                    if (entityType.GetDeclaredQueryFilters().Count == 0)
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Entity '{entityType.DisplayName()}' does not have the required EF global query filter.");
                    }

                    return;
                case LiveEfTenantIsolationMode.RegisteredPredicate:
                    var binding = definition.TenantBinding!;
                    if (!hasPredicate || entityType.FindProperty(binding.EntityProperty) is null)
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Registered tenant property '{binding.EntityProperty}' is absent from the EF entity or query predicate.");
                    }

                    if (!definition.ValidationArguments.Values.TryGetValue(binding.ParameterName, out var expectedValue))
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Registered tenant parameter '{binding.ParameterName}' is not declared by the Live query.");
                    }

                    if (!predicates.Any(predicate => TenantPredicateMatcher.Matches(
                            predicate,
                            binding.EntityProperty,
                            expectedValue)))
                    {
                        throw new LiveEfQueryRegistrationException(
                            $"Live query predicate does not bind entity property '{binding.EntityProperty}' to parameter '{binding.ParameterName}'.");
                    }

                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(definition));
            }
        }

        private static bool IsQueryableChainMethod(MethodCallExpression methodCall) =>
            methodCall.Arguments.Count != 0 &&
            typeof(IQueryable).IsAssignableFrom(methodCall.Arguments[0].Type);

        private static LambdaExpression RequireLambda(Expression expression, string role)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            {
                expression = quote.Operand;
            }

            return expression as LambdaExpression ??
                throw new LiveEfQueryRegistrationException(
                    $"The Live query {role} is not a lambda expression.");
        }

        private static int EvaluateTake(Expression expression)
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
                    "The Live query Take bound could not be evaluated during registration.",
                    exception);
            }
        }
    }

    private sealed class SimplePredicateValidator : ExpressionVisitor
    {
        private static readonly HashSet<ExpressionType> AllowedBinaryNodes =
        [
            ExpressionType.Equal,
            ExpressionType.NotEqual,
            ExpressionType.GreaterThan,
            ExpressionType.GreaterThanOrEqual,
            ExpressionType.LessThan,
            ExpressionType.LessThanOrEqual,
            ExpressionType.AndAlso,
            ExpressionType.OrElse,
        ];

        public static void Validate(Expression expression) => new SimplePredicateValidator().Visit(expression);

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (!AllowedBinaryNodes.Contains(node.NodeType))
            {
                throw Unsupported(node);
            }

            return base.VisitBinary(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType is not ExpressionType.Convert and
                not ExpressionType.ConvertChecked and
                not ExpressionType.Not)
            {
                throw Unsupported(node);
            }

            return base.VisitUnary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var supportedStringMethod = node.Method.DeclaringType == typeof(string) &&
                node.Method.Name is nameof(string.Contains) or nameof(string.StartsWith) or nameof(string.EndsWith);
            var supportedEfProperty = node.Method.DeclaringType == typeof(EF) &&
                node.Method.Name == nameof(EF.Property);
            if (!supportedStringMethod && !supportedEfProperty)
            {
                throw Unsupported(node);
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitNew(NewExpression node) => throw Unsupported(node);

        protected override Expression VisitInvocation(InvocationExpression node) => throw Unsupported(node);

        private static LiveEfQueryRegistrationException Unsupported(Expression node) =>
            new($"Predicate expression '{node.NodeType}' is not supported by the initial Live compiler.");
    }

    private sealed class TenantPredicateMatcher(
        ParameterExpression entityParameter,
        string propertyName,
        object? expectedValue) : ExpressionVisitor
    {
        private bool _matched;

        public static bool Matches(
            LambdaExpression predicate,
            string propertyName,
            object? expectedValue)
        {
            var matcher = new TenantPredicateMatcher(predicate.Parameters[0], propertyName, expectedValue);
            matcher.Visit(predicate.Body);
            return matcher._matched;
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Equal &&
                ((IsEntityProperty(node.Left) && ValueMatches(node.Right)) ||
                 (IsEntityProperty(node.Right) && ValueMatches(node.Left))))
            {
                _matched = true;
            }

            return base.VisitBinary(node);
        }

        private bool IsEntityProperty(Expression expression)
        {
            expression = StripConvert(expression);
            return expression is MemberExpression member &&
                member.Expression == entityParameter &&
                string.Equals(member.Member.Name, propertyName, StringComparison.Ordinal);
        }

        private bool ValueMatches(Expression expression)
        {
            try
            {
                var converted = Expression.Convert(expression, typeof(object));
                var actual = Expression.Lambda<Func<object?>>(converted).Compile().Invoke();
                return Equals(actual, expectedValue);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}

public sealed class LiveEfQueryRegistrationException : LiveQueryException
{
    public LiveEfQueryRegistrationException(string message)
        : base(message)
    {
    }

    public LiveEfQueryRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
