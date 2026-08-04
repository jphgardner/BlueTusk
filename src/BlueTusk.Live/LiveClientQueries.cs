using System.Buffers;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueTusk.Live;

public enum LiveClientQueryLanguage
{
    Sql,
    Linq,
}

[Flags]
public enum LiveClientSecurityMode
{
    None = 0,
    DatabaseRowLevelSecurity = 1 << 0,
    DedicatedReadOnlyRole = 1 << 1,
}

public enum LiveClientFilterOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    StartsWith,
    Contains,
    IsNull,
    IsNotNull,
}

public enum LiveClientSortDirection
{
    Ascending,
    Descending,
}

/// <summary>An allowlisted relation and its queryable columns.</summary>
public sealed class LiveClientRelation
{
    private readonly ReadOnlyCollection<string> _columns;

    public LiveClientRelation(
        string schema,
        string table,
        IEnumerable<string> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(columns);
        var materialized = columns
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException(
                "A client-query relation must allow at least one column.",
                nameof(columns));
        }

        Schema = ValidateIdentifier(schema);
        Table = ValidateIdentifier(table);
        _columns = Array.AsReadOnly(materialized);
    }

    public string Schema { get; }

    public string Table { get; }

    public IReadOnlyList<string> Columns => _columns;

    internal string Identity => Schema + "." + Table;

    internal bool Allows(string column) =>
        _columns.Contains(column, StringComparer.Ordinal);

    internal static string ValidateIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Length > 63 ||
            identifier.Any(static character =>
                character is '\0' or '\r' or '\n' ||
                char.IsControl(character)))
        {
            throw new ArgumentException(
                "A PostgreSQL identifier must contain at most 63 non-control characters.",
                nameof(identifier));
        }

        return identifier;
    }
}

public sealed class LiveClientFilter
{
    public LiveClientFilter(
        string column,
        LiveClientFilterOperator @operator,
        string? parameter = null)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        Column = LiveClientRelation.ValidateIdentifier(column);
        if (@operator is LiveClientFilterOperator.IsNull or LiveClientFilterOperator.IsNotNull)
        {
            if (parameter is not null)
            {
                throw new ArgumentException(
                    "Null-test filters cannot bind a parameter.",
                    nameof(parameter));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
            ValidateParameterName(parameter);
        }

        Operator = @operator;
        Parameter = parameter;
    }

    public string Column { get; }

    public LiveClientFilterOperator Operator { get; }

    public string? Parameter { get; }

    internal static void ValidateParameterName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 ||
            !(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
            name.Skip(1).Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "A client-query parameter name must be a simple ASCII identifier of at most 128 characters.",
                nameof(name));
        }
    }
}

public sealed class LiveClientOrdering
{
    public LiveClientOrdering(
        string column,
        LiveClientSortDirection direction = LiveClientSortDirection.Ascending)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        Column = LiveClientRelation.ValidateIdentifier(column);
        Direction = direction;
    }

    public string Column { get; }

    public LiveClientSortDirection Direction { get; }
}

/// <summary>A finite remote LINQ document compiled to one parameterized PostgreSQL query.</summary>
public sealed class LiveClientLinqQuery
{
    private readonly ReadOnlyCollection<string> _columns;
    private readonly ReadOnlyCollection<LiveClientFilter> _filters;
    private readonly ReadOnlyCollection<LiveClientOrdering> _orderings;

    public LiveClientLinqQuery(
        string schema,
        string table,
        IEnumerable<string> columns,
        IEnumerable<LiveClientFilter> filters,
        IEnumerable<LiveClientOrdering> orderings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(orderings);
        var materializedColumns = columns
            .Select(LiveClientRelation.ValidateIdentifier)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var materializedFilters = filters.ToArray();
        var materializedOrderings = orderings.ToArray();
        if (materializedColumns.Length == 0)
        {
            throw new ArgumentException(
                "A remote LINQ query must project at least one column.",
                nameof(columns));
        }

        if (materializedFilters.Any(static filter => filter is null))
        {
            throw new ArgumentException(
                "Remote LINQ filters cannot contain null.",
                nameof(filters));
        }

        if (materializedOrderings.Length == 0 ||
            materializedOrderings.Any(static ordering => ordering is null))
        {
            throw new ArgumentException(
                "A remote LINQ query requires at least one non-null ordering.",
                nameof(orderings));
        }

        Schema = LiveClientRelation.ValidateIdentifier(schema);
        Table = LiveClientRelation.ValidateIdentifier(table);
        _columns = Array.AsReadOnly(materializedColumns);
        _filters = Array.AsReadOnly(materializedFilters);
        _orderings = Array.AsReadOnly(materializedOrderings);
    }

    public string Schema { get; }

    public string Table { get; }

    public IReadOnlyList<string> Columns => _columns;

    public IReadOnlyList<LiveClientFilter> Filters => _filters;

    public IReadOnlyList<LiveClientOrdering> Orderings => _orderings;
}

/// <summary>An immutable client-authored query shape, separate from its bound values.</summary>
public sealed class LiveClientQueryDefinition
{
    private readonly ReadOnlyCollection<LiveQueryParameter> _parameters;
    private readonly ReadOnlyCollection<string> _keyColumns;

    private LiveClientQueryDefinition(
        string name,
        string version,
        LiveClientQueryLanguage language,
        string? sql,
        LiveClientLinqQuery? linq,
        IEnumerable<LiveQueryParameter> parameters,
        IEnumerable<string> keyColumns,
        int maximumResultCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultCount);
        if (maximumResultCount == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResultCount),
                "A client-query result bound must leave room for one overflow-detection row.");
        }
        var materializedParameters = parameters.ToArray();
        var materializedKeys = keyColumns
            .Select(LiveClientRelation.ValidateIdentifier)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (materializedKeys.Length == 0)
        {
            throw new ArgumentException(
                "A client query must identify at least one result key column.",
                nameof(keyColumns));
        }

        if (materializedParameters.Select(static parameter => parameter.Name)
            .Distinct(StringComparer.Ordinal).Count() != materializedParameters.Length)
        {
            throw new ArgumentException(
                "Client-query parameter names must be unique.",
                nameof(parameters));
        }

        foreach (var parameter in materializedParameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            LiveClientFilter.ValidateParameterName(parameter.Name);
        }

        Name = name;
        Version = version;
        Language = language;
        Sql = sql;
        Linq = linq;
        _parameters = Array.AsReadOnly(materializedParameters);
        _keyColumns = Array.AsReadOnly(materializedKeys);
        MaximumResultCount = maximumResultCount;
    }

    public string Name { get; }

    public string Version { get; }

    public LiveClientQueryLanguage Language { get; }

    public string? Sql { get; }

    public LiveClientLinqQuery? Linq { get; }

    public IReadOnlyList<LiveQueryParameter> Parameters => _parameters;

    public IReadOnlyList<string> KeyColumns => _keyColumns;

    public int MaximumResultCount { get; }

    public static LiveClientQueryDefinition CreateSql(
        string name,
        string version,
        string sql,
        IEnumerable<LiveQueryParameter> parameters,
        IEnumerable<string> keyColumns,
        int maximumResultCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return new LiveClientQueryDefinition(
            name,
            version,
            LiveClientQueryLanguage.Sql,
            sql,
            null,
            parameters,
            keyColumns,
            maximumResultCount);
    }

    public static LiveClientQueryDefinition CreateLinq(
        string name,
        string version,
        LiveClientLinqQuery query,
        IEnumerable<LiveQueryParameter> parameters,
        IEnumerable<string> keyColumns,
        int maximumResultCount)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new LiveClientQueryDefinition(
            name,
            version,
            LiveClientQueryLanguage.Linq,
            null,
            query,
            parameters,
            keyColumns,
            maximumResultCount);
    }
}

/// <summary>A trusted application-issued capability grant for client-authored queries.</summary>
public sealed class LiveClientQueryPolicy
{
    private readonly ReadOnlyCollection<LiveClientRelation> _relations;

    public LiveClientQueryPolicy(
        string name,
        string version,
        string databaseIdentity,
        LiveClientSecurityMode securityMode,
        IEnumerable<LiveClientRelation> relations,
        bool allowSql = false,
        int maximumQueryBytes = 32 * 1024,
        int maximumParameters = 64,
        int maximumResultCount = 1_000,
        int maximumResultColumns = 128,
        long maximumResultBytes = 8L * 1024 * 1024,
        TimeSpan? statementTimeout = null,
        TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        const LiveClientSecurityMode supportedSecurityModes =
            LiveClientSecurityMode.DatabaseRowLevelSecurity |
            LiveClientSecurityMode.DedicatedReadOnlyRole;
        if (securityMode is LiveClientSecurityMode.None ||
            (securityMode & ~supportedSecurityModes) is not LiveClientSecurityMode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(securityMode));
        }

        ArgumentNullException.ThrowIfNull(relations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParameters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultCount);
        if (maximumResultCount == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResultCount),
                "A client-query policy must leave room for one overflow-detection row.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultColumns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultBytes);
        statementTimeout ??= TimeSpan.FromSeconds(5);
        lockTimeout ??= TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(statementTimeout.Value, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockTimeout.Value, TimeSpan.Zero);
        if (statementTimeout.Value > TimeSpan.FromMinutes(5) ||
            lockTimeout.Value > statementTimeout.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statementTimeout),
                "Client-query timeouts must be at most five minutes and lock timeout cannot exceed statement timeout.");
        }

        var materialized = relations.ToArray();
        if (materialized.Length == 0 || materialized.Any(static relation => relation is null))
        {
            throw new ArgumentException(
                "A client-query policy must contain at least one non-null relation.",
                nameof(relations));
        }

        if (materialized.Select(static relation => relation.Identity)
            .Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException(
                "A client-query policy cannot contain duplicate relations.",
                nameof(relations));
        }

        Name = name;
        Version = version;
        DatabaseIdentity = databaseIdentity;
        SecurityMode = securityMode;
        _relations = Array.AsReadOnly(materialized
            .OrderBy(static relation => relation.Schema, StringComparer.Ordinal)
            .ThenBy(static relation => relation.Table, StringComparer.Ordinal)
            .ToArray());
        AllowSql = allowSql;
        MaximumQueryBytes = maximumQueryBytes;
        MaximumParameters = maximumParameters;
        MaximumResultCount = maximumResultCount;
        MaximumResultColumns = maximumResultColumns;
        MaximumResultBytes = maximumResultBytes;
        StatementTimeout = statementTimeout.Value;
        LockTimeout = lockTimeout.Value;
    }

    public string Name { get; }

    public string Version { get; }

    public string DatabaseIdentity { get; }

    public LiveClientSecurityMode SecurityMode { get; }

    public IReadOnlyList<LiveClientRelation> Relations => _relations;

    public bool AllowSql { get; }

    public int MaximumQueryBytes { get; }

    public int MaximumParameters { get; }

    public int MaximumResultCount { get; }

    public int MaximumResultColumns { get; }

    public long MaximumResultBytes { get; }

    public TimeSpan StatementTimeout { get; }

    public TimeSpan LockTimeout { get; }
}

public sealed class LiveClientRow
{
    private readonly ReadOnlyDictionary<string, JsonElement> _values;

    internal LiveClientRow(
        IDictionary<string, JsonElement> values,
        string fingerprint)
    {
        _values = new ReadOnlyDictionary<string, JsonElement>(values);
        Fingerprint = fingerprint;
    }

    [JsonPropertyName("values")]
    public IReadOnlyDictionary<string, JsonElement> Values => _values;

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; }

    public T? Get<T>(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (!_values.TryGetValue(column, out var value))
        {
            throw new KeyNotFoundException(
                $"Client-query result column '{column}' does not exist.");
        }

        return value.Deserialize<T>();
    }

    internal string CreateKey(IReadOnlyList<string> columns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var column in columns)
        {
            if (!_values.TryGetValue(column, out var value))
            {
                throw new LiveClientQueryExecutionException(
                    $"Client-query key column '{column}' is absent from the result.");
            }

            Append(hash, column);
            Append(hash, value.GetRawText());
        }

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

public class LiveClientQueryException : LiveQueryException
{
    public LiveClientQueryException(string message)
        : base(message)
    {
    }

    public LiveClientQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LiveClientQueryRegistrationException : LiveClientQueryException
{
    public LiveClientQueryRegistrationException(string message)
        : base(message)
    {
    }

    public LiveClientQueryRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LiveClientQueryExecutionException : LiveClientQueryException
{
    public LiveClientQueryExecutionException(string message)
        : base(message)
    {
    }

    public LiveClientQueryExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class LiveClientQueryCompiler
{
    private static readonly string[] DeniedSqlTokens =
    [
        "alter",
        "analyze",
        "call",
        "comment",
        "copy",
        "create",
        "delete",
        "discard",
        "do",
        "drop",
        "execute",
        "grant",
        "insert",
        "listen",
        "load",
        "lock",
        "merge",
        "notify",
        "prepare",
        "reassign",
        "refresh",
        "reindex",
        "reset",
        "revoke",
        "security",
        "set",
        "show",
        "truncate",
        "unlisten",
        "update",
        "vacuum",
    ];

    private static readonly string[] DeniedSqlFunctions =
    [
        "dblink",
        "lo_export",
        "lo_import",
        "nextval",
        "pg_advisory_lock",
        "pg_advisory_xact_lock",
        "pg_cancel_backend",
        "pg_ls_dir",
        "pg_read_binary_file",
        "pg_read_file",
        "pg_reload_conf",
        "pg_rotate_logfile",
        "pg_sleep",
        "pg_stat_file",
        "pg_terminate_backend",
        "set_config",
        "setval",
    ];

    public static LiveQueryPlan<LiveClientRow, string> Compile(
        DbDataSource dataSource,
        LiveClientQueryPolicy policy,
        LiveClientQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Parameters.Count > policy.MaximumParameters)
        {
            throw new LiveClientQueryRegistrationException(
                $"Client query declares {definition.Parameters.Count} parameters; policy '{policy.Name}' allows {policy.MaximumParameters}.");
        }

        if (definition.MaximumResultCount > policy.MaximumResultCount)
        {
            throw new LiveClientQueryRegistrationException(
                $"Client query result bound {definition.MaximumResultCount} exceeds policy '{policy.Name}' limit {policy.MaximumResultCount}.");
        }

        var compiled = definition.Language switch
        {
            LiveClientQueryLanguage.Sql => CompileSql(policy, definition),
            LiveClientQueryLanguage.Linq => CompileLinq(policy, definition),
            _ => throw new LiveClientQueryRegistrationException(
                $"Unsupported client-query language '{definition.Language}'."),
        };
        var canonical = CreateCanonicalPlan(policy, definition, compiled);
        var fingerprint = LiveQueryFingerprint.Create(
            definition.Name,
            definition.Version,
            canonical);
        var capabilities =
            LiveQueryCapabilities.ParameterizedPredicate |
            LiveQueryCapabilities.TenantFilter |
            LiveQueryCapabilities.DeterministicOrdering |
            LiveQueryCapabilities.BoundedTake;
        if (compiled.Dependencies.Count == 1)
        {
            capabilities |= LiveQueryCapabilities.SingleTable;
        }

        var rowComparer = new LiveClientRowComparer();
        return new LiveQueryPlan<LiveClientRow, string>(
            definition.Name,
            policy.DatabaseIdentity,
            fingerprint,
            capabilities,
            compiled.Dependencies,
            definition.Parameters,
            definition.MaximumResultCount,
            (execution, token) => ExecuteAsync(
                dataSource,
                policy,
                definition,
                compiled.Sql,
                execution.Arguments,
                token),
            row => row.CreateKey(definition.KeyColumns),
            rowComparer);
    }

    private static CompiledQuery CompileSql(
        LiveClientQueryPolicy policy,
        LiveClientQueryDefinition definition)
    {
        if (!policy.AllowSql)
        {
            throw new LiveClientQueryRegistrationException(
                $"Client SQL is disabled by policy '{policy.Name}'.");
        }

        const LiveClientSecurityMode requiredSecurityModes =
            LiveClientSecurityMode.DatabaseRowLevelSecurity |
            LiveClientSecurityMode.DedicatedReadOnlyRole;
        if ((policy.SecurityMode & requiredSecurityModes) != requiredSecurityModes)
        {
            throw new LiveClientQueryRegistrationException(
                $"Client SQL policy '{policy.Name}' must require both database row-level security and a dedicated read-only role.");
        }

        var sql = definition.Sql ??
            throw new LiveClientQueryRegistrationException(
                "A SQL client query has no SQL text.");
        if (Encoding.UTF8.GetByteCount(sql) > policy.MaximumQueryBytes)
        {
            throw new LiveClientQueryRegistrationException(
                $"Client SQL exceeds policy '{policy.Name}' query-size limit.");
        }

        ValidateSql(sql);
        var innerSql = sql.Trim().TrimEnd(';').TrimEnd();
        var bounded = new StringBuilder(innerSql.Length + 128);
        bounded.Append("SELECT * FROM (");
        bounded.Append(innerSql);
        bounded.Append(") AS \"__live_client_query\" ORDER BY ");
        for (var index = 0; index < definition.KeyColumns.Count; index++)
        {
            if (index > 0)
            {
                bounded.Append(", ");
            }

            bounded.Append("\"__live_client_query\".");
            AppendIdentifier(bounded, definition.KeyColumns[index]);
        }

        bounded.Append(" LIMIT ");
        bounded.Append(
            checked(definition.MaximumResultCount + 1)
            .ToString(CultureInfo.InvariantCulture));
        if (Encoding.UTF8.GetByteCount(bounded.ToString()) > policy.MaximumQueryBytes)
        {
            throw new LiveClientQueryRegistrationException(
                $"Bounded client SQL exceeds policy '{policy.Name}' query-size limit.");
        }

        var dependencies = policy.Relations
            .Select(static relation => new LiveTableDependency(relation.Schema, relation.Table))
            .ToArray();
        return new CompiledQuery(bounded.ToString(), dependencies);
    }

    private static CompiledQuery CompileLinq(
        LiveClientQueryPolicy policy,
        LiveClientQueryDefinition definition)
    {
        var query = definition.Linq ??
            throw new LiveClientQueryRegistrationException(
                "A LINQ client query has no query document.");
        var relation = policy.Relations.SingleOrDefault(item =>
            string.Equals(item.Schema, query.Schema, StringComparison.Ordinal) &&
            string.Equals(item.Table, query.Table, StringComparison.Ordinal)) ??
            throw new LiveClientQueryRegistrationException(
                $"Relation '{query.Schema}.{query.Table}' is not allowed by policy '{policy.Name}'.");
        var parameterNames = definition.Parameters
            .Select(static parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var column in query.Columns
                     .Concat(query.Filters.Select(static filter => filter.Column))
                     .Concat(query.Orderings.Select(static ordering => ordering.Column))
                     .Concat(definition.KeyColumns))
        {
            if (!relation.Allows(column))
            {
                throw new LiveClientQueryRegistrationException(
                    $"Column '{column}' is not allowed on relation '{relation.Identity}'.");
            }
        }

        if (definition.KeyColumns.Any(key =>
                !query.Columns.Contains(key, StringComparer.Ordinal)))
        {
            throw new LiveClientQueryRegistrationException(
                "Every client-query key column must be present in the projection.");
        }

        if (definition.KeyColumns.Any(key =>
                !query.Orderings.Any(ordering =>
                    string.Equals(ordering.Column, key, StringComparison.Ordinal))))
        {
            throw new LiveClientQueryRegistrationException(
                "Remote LINQ ordering must include every result key column.");
        }

        foreach (var filter in query.Filters)
        {
            if (filter.Parameter is { } parameter &&
                !parameterNames.Contains(parameter))
            {
                throw new LiveClientQueryRegistrationException(
                    $"Remote LINQ filter parameter '{parameter}' is not declared.");
            }
        }

        var buffer = new StringBuilder();
        buffer.Append("SELECT ");
        AppendIdentifiers(buffer, query.Columns);
        buffer.Append(" FROM ");
        AppendIdentifier(buffer, relation.Schema);
        buffer.Append('.');
        AppendIdentifier(buffer, relation.Table);
        if (query.Filters.Count > 0)
        {
            buffer.Append(" WHERE ");
            for (var index = 0; index < query.Filters.Count; index++)
            {
                if (index > 0)
                {
                    buffer.Append(" AND ");
                }

                AppendFilter(buffer, query.Filters[index]);
            }
        }

        buffer.Append(" ORDER BY ");
        for (var index = 0; index < query.Orderings.Count; index++)
        {
            if (index > 0)
            {
                buffer.Append(", ");
            }

            AppendIdentifier(buffer, query.Orderings[index].Column);
            buffer.Append(query.Orderings[index].Direction is LiveClientSortDirection.Ascending
                ? " ASC"
                : " DESC");
        }

        buffer.Append(" LIMIT ");
        buffer.Append(definition.MaximumResultCount.ToString(CultureInfo.InvariantCulture));
        if (Encoding.UTF8.GetByteCount(buffer.ToString()) > policy.MaximumQueryBytes)
        {
            throw new LiveClientQueryRegistrationException(
                $"Compiled remote LINQ exceeds policy '{policy.Name}' query-size limit.");
        }

        return new CompiledQuery(
            buffer.ToString(),
            [new LiveTableDependency(relation.Schema, relation.Table)]);
    }

    private static void ValidateSql(string sql)
    {
        var tokens = TokenizeSql(sql);
        if (tokens.Count == 0 ||
            tokens[0] is not ("select" or "with"))
        {
            throw new LiveClientQueryRegistrationException(
                "Client SQL must be one SELECT or WITH query.");
        }

        if (tokens.Any(token => DeniedSqlTokens.Contains(token, StringComparer.Ordinal)))
        {
            throw new LiveClientQueryRegistrationException(
                "Client SQL contains a statement or clause outside the read-only query subset.");
        }

        if (tokens.Any(token => DeniedSqlFunctions.Contains(token, StringComparer.Ordinal)))
        {
            throw new LiveClientQueryRegistrationException(
                "Client SQL invokes a side-effecting or resource-control server function.");
        }

        if (tokens.Contains("for", StringComparer.Ordinal) &&
            (tokens.Contains("update", StringComparer.Ordinal) ||
             tokens.Contains("share", StringComparer.Ordinal)))
        {
            throw new LiveClientQueryRegistrationException(
                "Client SQL cannot acquire row locks.");
        }
    }

    private static List<string> TokenizeSql(string sql)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quotedString = false;
        var quotedIdentifier = false;
        var sawTerminator = false;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (character == '\0')
            {
                throw new LiveClientQueryRegistrationException(
                    "Client SQL cannot contain a null character.");
            }

            if (quotedString)
            {
                if (character == '\'' &&
                    index + 1 < sql.Length &&
                    sql[index + 1] == '\'')
                {
                    index++;
                }
                else if (character == '\'')
                {
                    quotedString = false;
                }

                continue;
            }

            if (quotedIdentifier)
            {
                if (character == '"' &&
                    index + 1 < sql.Length &&
                    sql[index + 1] == '"')
                {
                    index++;
                }
                else if (character == '"')
                {
                    quotedIdentifier = false;
                }

                continue;
            }

            if (character == '\'')
            {
                FlushToken(token, tokens);
                quotedString = true;
                continue;
            }

            if (character == '"')
            {
                FlushToken(token, tokens);
                quotedIdentifier = true;
                continue;
            }

            if (character == '-' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '-' ||
                character == '/' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '*')
            {
                throw new LiveClientQueryRegistrationException(
                    "Client SQL comments are not accepted.");
            }

            if (character == '$' &&
                index + 1 < sql.Length &&
                (sql[index + 1] == '$' || char.IsAsciiLetterOrDigit(sql[index + 1])))
            {
                throw new LiveClientQueryRegistrationException(
                    "Client SQL dollar quoting and positional parameters are not accepted; use named parameters.");
            }

            if (character == ';')
            {
                FlushToken(token, tokens);
                if (sawTerminator ||
                    sql.AsSpan(index + 1).IndexOfAnyExcept(" \t\r\n") >= 0)
                {
                    throw new LiveClientQueryRegistrationException(
                        "Client SQL must contain exactly one statement.");
                }

                sawTerminator = true;
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character) || character == '_')
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else
            {
                FlushToken(token, tokens);
            }
        }

        if (quotedString || quotedIdentifier)
        {
            throw new LiveClientQueryRegistrationException(
                "Client SQL contains an unterminated quoted value.");
        }

        FlushToken(token, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder token, List<string> tokens)
    {
        if (token.Length == 0)
        {
            return;
        }

        tokens.Add(token.ToString());
        token.Clear();
    }

    private static void AppendFilter(StringBuilder builder, LiveClientFilter filter)
    {
        AppendIdentifier(builder, filter.Column);
        switch (filter.Operator)
        {
            case LiveClientFilterOperator.Equal:
                builder.Append(" IS NOT DISTINCT FROM @");
                builder.Append(filter.Parameter);
                break;
            case LiveClientFilterOperator.NotEqual:
                builder.Append(" IS DISTINCT FROM @");
                builder.Append(filter.Parameter);
                break;
            case LiveClientFilterOperator.LessThan:
                builder.Append(" < @");
                builder.Append(filter.Parameter);
                break;
            case LiveClientFilterOperator.LessThanOrEqual:
                builder.Append(" <= @");
                builder.Append(filter.Parameter);
                break;
            case LiveClientFilterOperator.GreaterThan:
                builder.Append(" > @");
                builder.Append(filter.Parameter);
                break;
            case LiveClientFilterOperator.GreaterThanOrEqual:
                builder.Append(" >= @");
                builder.Append(filter.Parameter);
                break;
            case LiveClientFilterOperator.StartsWith:
                builder.Append(" LIKE @");
                builder.Append(filter.Parameter);
                builder.Append(" || '%'");
                break;
            case LiveClientFilterOperator.Contains:
                builder.Append(" LIKE '%' || @");
                builder.Append(filter.Parameter);
                builder.Append(" || '%'");
                break;
            case LiveClientFilterOperator.IsNull:
                builder.Append(" IS NULL");
                break;
            case LiveClientFilterOperator.IsNotNull:
                builder.Append(" IS NOT NULL");
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported remote LINQ filter operator '{filter.Operator}'.");
        }
    }

    private static void AppendIdentifiers(
        StringBuilder builder,
        IReadOnlyList<string> identifiers)
    {
        for (var index = 0; index < identifiers.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendIdentifier(builder, identifiers[index]);
        }
    }

    private static void AppendIdentifier(StringBuilder builder, string identifier)
    {
        builder.Append('"');
        builder.Append(identifier.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    private static byte[] CreateCanonicalPlan(
        LiveClientQueryPolicy policy,
        LiveClientQueryDefinition definition,
        CompiledQuery compiled)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            writer.WriteString("policy", policy.Name);
            writer.WriteString("policyVersion", policy.Version);
            writer.WriteString("database", policy.DatabaseIdentity);
            writer.WriteString("securityMode", policy.SecurityMode.ToString());
            writer.WriteBoolean("allowSql", policy.AllowSql);
            writer.WriteNumber("maximumQueryBytes", policy.MaximumQueryBytes);
            writer.WriteNumber("maximumParameters", policy.MaximumParameters);
            writer.WriteNumber("maximumResultCount", policy.MaximumResultCount);
            writer.WriteNumber("maximumResultColumns", policy.MaximumResultColumns);
            writer.WriteNumber("maximumResultBytes", policy.MaximumResultBytes);
            writer.WriteNumber("statementTimeoutTicks", policy.StatementTimeout.Ticks);
            writer.WriteNumber("lockTimeoutTicks", policy.LockTimeout.Ticks);
            writer.WriteString("language", definition.Language.ToString());
            writer.WriteString("sql", compiled.Sql);
            writer.WriteStartArray("dependencies");
            foreach (var dependency in compiled.Dependencies)
            {
                writer.WriteStringValue(dependency.ToString());
            }

            writer.WriteEndArray();
            writer.WriteStartArray("parameters");
            foreach (var parameter in definition.Parameters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", parameter.Name);
                writer.WriteString(
                    "type",
                    (Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType)
                    .AssemblyQualifiedName);
                writer.WriteBoolean("allowNull", parameter.AllowNull);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("keys");
            foreach (var key in definition.KeyColumns)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteNumber("resultCount", definition.MaximumResultCount);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static async ValueTask<IReadOnlyList<LiveClientRow>> ExecuteAsync(
        DbDataSource dataSource,
        LiveClientQueryPolicy policy,
        LiveClientQueryDefinition definition,
        string sql,
        LiveQueryArguments arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(policy.StatementTimeout + TimeSpan.FromSeconds(1));
        var token = timeout.Token;
        try
        {
            await using var connection = dataSource.CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                token).ConfigureAwait(false);
            await ConfigureReadOnlyTransactionAsync(
                connection,
                transaction,
                policy,
                token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = Math.Max(
                1,
                checked((int)Math.Ceiling(policy.StatementTimeout.TotalSeconds)));
            foreach (var parameterDefinition in definition.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = parameterDefinition.Name;
                parameter.Value = arguments.Values[parameterDefinition.Name] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            var rows = new List<LiveClientRow>(
                Math.Min(definition.MaximumResultCount, 256));
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                token).ConfigureAwait(false);
            var columnCount = reader.FieldCount;
            if (columnCount <= 0 || columnCount > policy.MaximumResultColumns)
            {
                throw new LiveClientQueryExecutionException(
                    $"Client query returned {columnCount} columns; policy '{policy.Name}' permits between one and {policy.MaximumResultColumns}.");
            }

            var names = new string[columnCount];
            var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < columnCount; index++)
            {
                names[index] = reader.GetName(index);
                if (string.IsNullOrWhiteSpace(names[index]) ||
                    !uniqueNames.Add(names[index]))
                {
                    throw new LiveClientQueryExecutionException(
                        "Client-query result columns must have unique non-empty names.");
                }
            }

            long resultBytes = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (rows.Count >= definition.MaximumResultCount)
                {
                    throw new LiveClientQueryExecutionException(
                        $"Client query returned more than its declared bound of {definition.MaximumResultCount} rows.");
                }

                var values = new Dictionary<string, JsonElement>(
                    columnCount,
                    StringComparer.Ordinal);
                using var rowHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var index = 0; index < columnCount; index++)
                {
                    var value = await reader.IsDBNullAsync(index, token).ConfigureAwait(false)
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : SerializeValue(reader.GetValue(index), names[index]);
                    values.Add(names[index], value);
                    var raw = value.GetRawText();
                    resultBytes = checked(
                        resultBytes +
                        Encoding.UTF8.GetByteCount(names[index]) +
                        Encoding.UTF8.GetByteCount(raw));
                    if (resultBytes > policy.MaximumResultBytes)
                    {
                        throw new LiveClientQueryExecutionException(
                            $"Client-query result exceeds policy '{policy.Name}' byte limit {policy.MaximumResultBytes}.");
                    }

                    AppendHash(rowHash, names[index]);
                    AppendHash(rowHash, raw);
                }

                rows.Add(new LiveClientRow(
                    values,
                    Convert.ToHexStringLower(rowHash.GetHashAndReset())));
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return rows;
        }
        catch (LiveClientQueryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LiveClientQueryExecutionException(
                $"Client query exceeded policy '{policy.Name}' statement timeout.");
        }
        catch (Exception exception)
        {
            throw new LiveClientQueryExecutionException(
                $"Client query failed under policy '{policy.Name}'.",
                exception);
        }
    }

    private static async ValueTask ConfigureReadOnlyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        LiveClientQueryPolicy policy,
        CancellationToken cancellationToken)
    {
        await ExecuteControlCommandAsync(
            connection,
            transaction,
            "SET TRANSACTION READ ONLY",
            cancellationToken).ConfigureAwait(false);
        await ExecuteControlCommandAsync(
            connection,
            transaction,
            "SET LOCAL row_security = on",
            cancellationToken).ConfigureAwait(false);
        await ExecuteControlCommandAsync(
            connection,
            transaction,
            $"SET LOCAL statement_timeout = {Milliseconds(policy.StatementTimeout)}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteControlCommandAsync(
            connection,
            transaction,
            $"SET LOCAL lock_timeout = {Milliseconds(policy.LockTimeout)}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteControlCommandAsync(
            connection,
            transaction,
            $"SET LOCAL idle_in_transaction_session_timeout = {Milliseconds(policy.StatementTimeout + TimeSpan.FromSeconds(1))}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteControlCommandAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long Milliseconds(TimeSpan value) =>
        checked((long)Math.Ceiling(value.TotalMilliseconds));

    private static JsonElement SerializeValue(object value, string column)
    {
        try
        {
            return value switch
            {
                JsonElement element => element.Clone(),
                ReadOnlyMemory<byte> memory => JsonSerializer.SerializeToElement(memory.ToArray()),
                Memory<byte> memory => JsonSerializer.SerializeToElement(memory.ToArray()),
                _ => JsonSerializer.SerializeToElement(value, value.GetType()),
            };
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            throw new LiveClientQueryExecutionException(
                $"Client-query column '{column}' has unsupported dynamic value type '{value.GetType()}'.",
                exception);
        }
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed record CompiledQuery(
        string Sql,
        IReadOnlyList<LiveTableDependency> Dependencies);

    private sealed class LiveClientRowComparer : IEqualityComparer<LiveClientRow>
    {
        public bool Equals(LiveClientRow? x, LiveClientRow? y) =>
            ReferenceEquals(x, y) ||
            x is not null &&
            y is not null &&
            string.Equals(x.Fingerprint, y.Fingerprint, StringComparison.Ordinal);

        public int GetHashCode(LiveClientRow obj) =>
            StringComparer.Ordinal.GetHashCode(obj.Fingerprint);
    }
}
