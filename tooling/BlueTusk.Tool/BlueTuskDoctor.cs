using System.Globalization;
using System.Text.Json;
using BlueTusk.Data;

namespace BlueTusk.Tool;

internal static class BlueTuskDoctor
{
    private const int MinimumSupportedPostgreSqlVersion = 150000;
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        DoctorOptions options,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var checks = new List<DoctorCheck>();

        try
        {
            await using var connection = new BlueTuskConnection(options.ConnectionString);
            await connection.OpenAsync(timeout.Token).ConfigureAwait(false);

            var versionNumber = await ScalarAsync<int>(
                connection,
                "SELECT current_setting('server_version_num')::int4",
                timeout.Token).ConfigureAwait(false);
            var database = await ScalarAsync<string>(
                connection,
                "SELECT current_database()",
                timeout.Token).ConfigureAwait(false);
            Add(
                checks,
                versionNumber >= MinimumSupportedPostgreSqlVersion,
                "postgresql",
                versionNumber >= MinimumSupportedPostgreSqlVersion ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
                $"PostgreSQL {FormatVersion(versionNumber)}; database '{database}'.");

            var tls = await ScalarAsync<bool>(
                connection,
                "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()",
                timeout.Token).ConfigureAwait(false);
            Add(
                checks,
                tls || !options.RequireTls,
                "tls",
                tls ? DoctorCheckStatus.Pass : options.RequireTls ? DoctorCheckStatus.Fail : DoctorCheckStatus.Warning,
                tls
                    ? "The active PostgreSQL session is encrypted."
                    : options.RequireTls
                        ? "The active PostgreSQL session is not encrypted, but TLS is required."
                        : "The active PostgreSQL session is not encrypted; use --require-tls for production enforcement.");

            if (options.RequireStreams)
            {
                var walLevel = await ScalarAsync<string>(
                    connection,
                    "SELECT current_setting('wal_level')",
                    timeout.Token).ConfigureAwait(false);
                var maxSlots = await ScalarAsync<int>(
                    connection,
                    "SELECT current_setting('max_replication_slots')::int4",
                    timeout.Token).ConfigureAwait(false);
                var maxSenders = await ScalarAsync<int>(
                    connection,
                    "SELECT current_setting('max_wal_senders')::int4",
                    timeout.Token).ConfigureAwait(false);
                var streamsReady = string.Equals(walLevel, "logical", StringComparison.Ordinal) &&
                    maxSlots > 0 &&
                    maxSenders > 0;
                Add(
                    checks,
                    streamsReady,
                    "streams",
                    streamsReady ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
                    $"wal_level={walLevel}; max_replication_slots={maxSlots}; max_wal_senders={maxSenders}.");
            }

            foreach (var extension in options.RequiredExtensions)
            {
                var installed = await ExtensionInstalledAsync(
                    connection,
                    extension,
                    timeout.Token).ConfigureAwait(false);
                Add(
                    checks,
                    installed,
                    $"extension:{extension}",
                    installed ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
                    installed
                        ? $"Extension '{extension}' is installed."
                        : $"Required extension '{extension}' is not installed.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            checks.Add(new DoctorCheck(
                "connection",
                DoctorCheckStatus.Fail,
                Sanitize(exception.Message, options.ConnectionString)));
        }
        catch (OperationCanceledException)
        {
            checks.Add(new DoctorCheck(
                "connection",
                DoctorCheckStatus.Fail,
                $"Diagnostics exceeded the {options.TimeoutSeconds}-second timeout."));
        }

        WriteReport(options, checks, output);
        if (checks.Any(static check => check.Status is DoctorCheckStatus.Fail))
        {
            if (!options.Json)
            {
                error.WriteLine("BlueTusk doctor found production-blocking failures.");
            }

            return 1;
        }

        return 0;
    }

    private static async ValueTask<T> ScalarAsync<T>(
        BlueTuskConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("PostgreSQL returned no value for a required diagnostic query.");
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<bool> ExtensionInstalledAsync(
        BlueTuskConnection connection,
        string extension,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = @name)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = extension;
        _ = command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static void Add(
        ICollection<DoctorCheck> checks,
        bool condition,
        string name,
        DoctorCheckStatus status,
        string message) =>
        checks.Add(new DoctorCheck(
            name,
            condition ? status : DoctorCheckStatus.Fail,
            message));

    private static void WriteReport(
        DoctorOptions options,
        IReadOnlyCollection<DoctorCheck> checks,
        TextWriter output)
    {
        var failures = checks.Count(static check => check.Status is DoctorCheckStatus.Fail);
        var warnings = checks.Count(static check => check.Status is DoctorCheckStatus.Warning);
        if (options.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    status = failures == 0 ? "ready" : "not-ready",
                    failures,
                    warnings,
                    checks = checks.Select(static check => new
                    {
                        check.Name,
                        status = check.Status.ToString().ToLowerInvariant(),
                        check.Message,
                    }),
                },
                ReportJsonOptions));
            return;
        }

        output.WriteLine("BlueTusk production diagnostics");
        foreach (var check in checks)
        {
            output.WriteLine($"{check.Status.ToString().ToUpperInvariant(),-7} {check.Name,-24} {check.Message}");
        }

        output.WriteLine();
        output.WriteLine($"Result: {(failures == 0 ? "READY" : "NOT READY")} ({failures} failed, {warnings} warning). ");
    }

    private static string FormatVersion(int versionNumber) =>
        $"{versionNumber / 10000}.{versionNumber / 100 % 100}";

    private static string Sanitize(string message, string connectionString) =>
        string.IsNullOrEmpty(connectionString)
            ? message
            : message.Replace(connectionString, "<connection string redacted>", StringComparison.Ordinal);

    private enum DoctorCheckStatus
    {
        Pass,
        Warning,
        Fail,
    }

    private sealed record DoctorCheck(string Name, DoctorCheckStatus Status, string Message);
}
