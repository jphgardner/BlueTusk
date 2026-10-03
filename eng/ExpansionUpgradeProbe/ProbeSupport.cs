using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueTusk.Data;

namespace BlueTusk.UpgradeProbe;

/// <summary>Shared phase context: the disposable fixture, durable state hand-off and the phase report.</summary>
internal sealed partial class ProbeContext : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private readonly SortedDictionary<string, long> _observations = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private readonly string _reportPath;
    private readonly string _statePath;

    public ProbeContext(string phase, string schemaBase, string statePath, string reportPath)
    {
        Require(SchemaPattern().IsMatch(schemaBase), "The probe schema base must be a short lowercase identifier.");
        Phase = phase;
        SchemaBase = schemaBase;
        _statePath = Path.GetFullPath(statePath);
        _reportPath = Path.GetFullPath(reportPath);
        Require(!File.Exists(_reportPath), "A phase report already exists; every phase needs a fresh report path.");
        Directory = Path.GetDirectoryName(_reportPath) ?? throw new InvalidOperationException("Report path has no directory.");
        ConnectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("An owned disposable PostgreSQL connection is required.");
        DataSource = BlueTuskDataSource.Create(ConnectionString);
    }

    /// <summary>
    /// Duration of every lease the baseline deliberately leaves held when it stops. It must exceed the
    /// seed-to-upgrade handoff (candidate start plus one initialization transaction, bounded by the
    /// store's documented 30-second command deadline), so twice that deadline. The verifier policy
    /// derives the upgrade phase wait from this constant.
    /// </summary>
    public const int InFlightLeaseSeconds = 60;

    public static TimeSpan InFlightLease { get; } = TimeSpan.FromSeconds(InFlightLeaseSeconds);

    public string Phase { get; }

    public string SchemaBase { get; }

    public string Directory { get; }

    public string ConnectionString { get; }

    public BlueTuskDataSource DataSource { get; }

    public bool IsSeed => Phase == "seed";

    public bool IsUpgrade => Phase == "upgrade";

    public bool IsRollback => Phase == "rollback";

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static async Task RequireRejectedAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    /// <summary>Records one exact effect count; the verifier compares every name and value with the policy.</summary>
    public void Observe(string name, long value)
    {
        Require(_observations.TryAdd(name, value), $"Observation '{name}' was recorded twice.");
    }

    /// <summary>Captures a canonical catalog description of the named schemas and retains it as raw evidence.</summary>
    public async Task FingerprintAsync(string label, params string[] schemas)
    {
        Require(schemas.Length > 0 && schemas.All(schema => SchemaPattern().IsMatch(schema)), "Fingerprinted schemas must be probe identifiers.");
        var names = string.Join(", ", schemas.Order(StringComparer.Ordinal).Select(schema => "'" + schema + "'"));
        var sql = $"""
            WITH target AS (SELECT oid, nspname FROM pg_namespace WHERE nspname IN ({names}))
            SELECT coalesce(string_agg(line, E'\n' ORDER BY line COLLATE "C"), '') FROM (
                SELECT 'schema|' || nspname AS line FROM target
                UNION ALL
                SELECT 'relation|' || t.nspname || '.' || c.relname || '|' || c.relkind::text || '|' || c.relpersistence::text
                FROM pg_class c JOIN target t ON t.oid = c.relnamespace
                UNION ALL
                SELECT 'column|' || t.nspname || '.' || c.relname || '|' || a.attnum::text || '|' || a.attname || '|' ||
                    format_type(a.atttypid, a.atttypmod) || '|' || a.attnotnull::text || '|' ||
                    coalesce(pg_get_expr(d.adbin, d.adrelid), '') || '|' || coalesce(co.collname, '')
                FROM pg_class c JOIN target t ON t.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
                LEFT JOIN pg_collation co ON co.oid = a.attcollation
                WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
                UNION ALL
                SELECT 'index|' || pg_get_indexdef(i.indexrelid)
                FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN target t ON t.oid = c.relnamespace
                UNION ALL
                SELECT 'constraint|' || t.nspname || '.' || coalesce(cl.relname, '') || '|' || con.conname || '|' ||
                    pg_get_constraintdef(con.oid)
                FROM pg_constraint con JOIN target t ON t.oid = con.connamespace
                LEFT JOIN pg_class cl ON cl.oid = con.conrelid
                UNION ALL
                SELECT 'routine|' || t.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) ||
                    ')|' || md5(pg_get_functiondef(p.oid))
                FROM pg_proc p JOIN target t ON t.oid = p.pronamespace WHERE p.prokind IN ('f', 'p')
                UNION ALL
                SELECT 'trigger|' || pg_get_triggerdef(tg.oid)
                FROM pg_trigger tg JOIN pg_class c ON c.oid = tg.tgrelid JOIN target t ON t.oid = c.relnamespace
                WHERE NOT tg.tgisinternal
                UNION ALL
                SELECT 'type|' || t.nspname || '.' || ty.typname || '|' || ty.typtype::text || '|' ||
                    coalesce(format_type(ty.typbasetype, ty.typtypmod), '')
                FROM pg_type ty JOIN target t ON t.oid = ty.typnamespace WHERE ty.typtype IN ('e', 'd')
                UNION ALL
                SELECT 'enum|' || t.nspname || '.' || ty.typname || '|' || e.enumsortorder::text || '|' || e.enumlabel
                FROM pg_enum e JOIN pg_type ty ON ty.oid = e.enumtypid JOIN target t ON t.oid = ty.typnamespace
            ) lines
            """;
        var catalog = (string)(await ScalarAsync(sql))!;
        // Only the seed phase may observe an absent schema; the verifier enforces which phase that is.
        var bytes = catalog.Length == 0 ? [] : Encoding.UTF8.GetBytes(catalog + "\n");
        var file = Path.Combine(Directory, $"{Phase}.catalog-{label}.txt");
        await File.WriteAllBytesAsync(file, bytes);
        Require(_fingerprints.TryAdd(label, Convert.ToHexStringLower(SHA256.HashData(bytes))), $"Fingerprint '{label}' was taken twice.");
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>Requires that a persisted expiry is still in the future by the database clock.</summary>
    public async Task RequireUnexpiredAsync(string expiryQuery, string what)
    {
        Require(await ScalarAsync($"SELECT ({expiryQuery}) > clock_timestamp()") is true,
            $"The {what} expired before the candidate opened the store; the handoff exceeded the in-flight lease budget.");
    }

    /// <summary>Waits until the database clock passes a persisted expiry, bounded by the in-flight lease.</summary>
    public async Task WaitForExpiryAsync(string expiryQuery, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(InFlightLeaseSeconds + 5);
        while (await ScalarAsync($"SELECT clock_timestamp() > ({expiryQuery})") is not true)
        {
            Require(DateTime.UtcNow < deadline, $"The {what} did not expire within its persisted lease duration.");
            await Task.Delay(250);
        }
    }

    public async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(sql), CultureInfo.InvariantCulture);

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync();
    }

    public async Task DropSchemaAsync(string schema)
    {
        Require(SchemaPattern().IsMatch(schema), "Only probe schemas may be dropped.");
        await ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
    }

    public Dictionary<string, string> ReadState()
    {
        Require(File.Exists(_statePath), "The phase hand-off state from the previous binary is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_statePath))
            ?? throw new InvalidOperationException("The phase hand-off state is empty.");
    }

    public void WriteState(Dictionary<string, string> state)
    {
        var temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new SortedDictionary<string, string>(state, StringComparer.Ordinal)));
        File.Move(temporary, _statePath, overwrite: true);
    }

    public async Task CompleteAsync()
    {
        var serverVersion = Convert.ToInt64(await ScalarAsync("SELECT current_setting('server_version_num')::bigint"), CultureInfo.InvariantCulture);
        Require(_observations.Count > 0, "A phase must record its effects.");
        Require(_fingerprints.ContainsKey("before") && _fingerprints.ContainsKey("after"),
            "A phase must fingerprint the durable schema before and after initialization.");
        var report = new
        {
            Family = FamilyProbe.Family,
            Phase,
            Passed = true,
            ServerVersionNum = serverVersion,
            Fingerprints = _fingerprints,
            Observations = _observations,
        };
        await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(report, ReportJson));
    }

    public async ValueTask DisposeAsync() => await DataSource.DisposeAsync();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaPattern();
}
