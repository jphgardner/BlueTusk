using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Streams.Storage.PostgreSql;

namespace BlueTusk.Streams.Tool;

/// <summary>Command-line validation and provisioning for BlueTusk Streams.</summary>
public static class BlueTuskStreamsCli
{
    private const string SourceEnvironmentVariable = "BLUETUSK_STREAMS_SOURCE";
    private const string ControlEnvironmentVariable = "BLUETUSK_STREAMS_CONTROL";

    /// <summary>Runs the BlueTusk Streams command line.</summary>
    public static int Run(
        IReadOnlyList<string> arguments,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        output ??= Console.Out;
        error ??= Console.Error;

        if (arguments.Count == 0 || arguments is ["--help"] or ["-h"])
        {
            WriteHelp(output);
            return 0;
        }

        var command = arguments[0];
        if (command is not ("validate" or "provision"))
        {
            error.WriteLine($"Unknown BlueTusk Streams command '{command}'.");
            error.WriteLine("Run 'bluetusk-streams --help' for usage.");
            return 2;
        }

        StreamsCliOptions options;
        try
        {
            options = ParseOptions(command, arguments.Skip(1).ToArray());
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine($"Run 'bluetusk-streams {command} --help' for usage.");
            return 2;
        }

        if (options.Help)
        {
            WriteCommandHelp(command, output);
            return 0;
        }

        try
        {
            return command == "validate"
                ? ValidateAsync(options, output).GetAwaiter().GetResult()
                : ProvisionAsync(options, output).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"BlueTusk Streams {command} failed: " +
                Sanitize(exception.Message, options.SourceConnection, options.ControlConnection));
            return 1;
        }
    }

    private static async Task<int> ValidateAsync(StreamsCliOptions options, TextWriter output)
    {
        await using var source = BlueTuskDataSource.Create(options.SourceConnection);
        await using var control = string.IsNullOrWhiteSpace(options.ControlConnection)
            ? null
            : BlueTuskDataSource.Create(options.ControlConnection);
        var report = await InspectAsync(source, control, options, CancellationToken.None).ConfigureAwait(false);
        WriteReport(report, output);
        return report.Any(static item => item.Severity == FindingSeverity.Error) ? 1 : 0;
    }

    private static async Task<int> ProvisionAsync(StreamsCliOptions options, TextWriter output)
    {
        await using var source = BlueTuskDataSource.Create(options.SourceConnection);
        await using var control = options.DirectOnly
            ? null
            : BlueTuskDataSource.Create(options.ControlConnection!);

        if (control is not null)
        {
            var sourceIdentity = await ReadDatabaseIdentityAsync(source, CancellationToken.None).ConfigureAwait(false);
            var controlIdentity = await ReadDatabaseIdentityAsync(control, CancellationToken.None).ConfigureAwait(false);
            if (sourceIdentity == controlIdentity && !options.AllowSharedControl)
            {
                throw new InvalidOperationException(
                    "The relay control data source resolves to the source database. " +
                    "Use a separate database or pass --allow-shared-control explicitly.");
            }

            if (sourceIdentity == controlIdentity && options.AllTables)
            {
                throw new InvalidOperationException(
                    "--all-tables cannot be combined with a relay control schema in the source database.");
            }
        }

        foreach (var publication in options.Publications)
        {
            if (!await PublicationExistsAsync(source, publication, CancellationToken.None).ConfigureAwait(false))
            {
                if (!options.AllTables && options.Tables.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Publication '{publication}' does not exist; specify --table or --all-tables to create it.");
                }

                await CreatePublicationAsync(source, publication, options, CancellationToken.None)
                    .ConfigureAwait(false);
                output.WriteLine($"CREATED publication {publication}");
            }
            else
            {
                output.WriteLine($"UNCHANGED publication {publication}");
            }
        }

        if (!options.SkipSlot)
        {
            var slot = await ReadSlotAsync(source, options.Slot, CancellationToken.None).ConfigureAwait(false);
            if (slot is null)
            {
                await CreateSlotAsync(source, options.Slot, CancellationToken.None).ConfigureAwait(false);
                output.WriteLine($"CREATED logical slot {options.Slot}");
            }
            else if (!string.Equals(slot.Value.Plugin, "pgoutput", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Slot '{options.Slot}' already exists with output plug-in '{slot.Value.Plugin}'.");
            }
            else
            {
                output.WriteLine($"UNCHANGED logical slot {options.Slot}");
            }
        }

        if (control is not null)
        {
            var storageOptions = new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = control,
                ControlSchema = options.ControlSchema,
            };
            var sourceIdentity = await ReadDatabaseIdentityAsync(source, CancellationToken.None).ConfigureAwait(false);
            var controlIdentity = await ReadDatabaseIdentityAsync(control, CancellationToken.None).ConfigureAwait(false);
            if (sourceIdentity == controlIdentity)
            {
                var publishedTables = await ReadPublishedTablesAsync(
                    source,
                    options.Publications,
                    CancellationToken.None).ConfigureAwait(false);
                PostgreSqlRelayPublicationValidator.Validate(storageOptions, publishedTables);
            }

            await new PostgreSqlDurableChangeRelay(storageOptions).InitializeAsync().ConfigureAwait(false);
            output.WriteLine($"READY relay schema {options.ControlSchema}");
        }
        else
        {
            output.WriteLine("SKIPPED relay storage (explicit --direct-only mode)");
        }

        var report = await InspectAsync(source, control, options, CancellationToken.None).ConfigureAwait(false);
        WriteReport(report, output);
        return report.Any(static item => item.Severity == FindingSeverity.Error) ? 1 : 0;
    }

    private static async Task<IReadOnlyList<Finding>> InspectAsync(
        DbDataSource source,
        DbDataSource? control,
        StreamsCliOptions options,
        CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var versionText = await ScalarStringAsync(source, "SHOW server_version_num", cancellationToken)
            .ConfigureAwait(false);
        var major = int.Parse(versionText, CultureInfo.InvariantCulture) / 10000;
        findings.Add(major is >= 15 and <= 19
            ? Finding.Ok("BTS001", $"PostgreSQL {major} is supported.")
            : Finding.Error("BTS001", $"PostgreSQL {major} is outside the supported 15-19 range."));

        var walLevel = await ScalarStringAsync(source, "SHOW wal_level", cancellationToken)
            .ConfigureAwait(false);
        findings.Add(string.Equals(walLevel, "logical", StringComparison.OrdinalIgnoreCase)
            ? Finding.Ok("BTS002", "wal_level is logical.")
            : Finding.Error("BTS002", $"wal_level is '{walLevel}'; logical is required."));

        foreach (var publication in options.Publications)
        {
            findings.Add(await PublicationExistsAsync(source, publication, cancellationToken).ConfigureAwait(false)
                ? Finding.Ok("BTS003", $"Publication '{publication}' exists.")
                : Finding.Error("BTS003", $"Publication '{publication}' does not exist."));
        }

        var tables = await ReadPublishedTablesAsync(source, options.Publications, cancellationToken)
            .ConfigureAwait(false);
        findings.Add(tables.Count == 0
            ? Finding.Error("BTS004", "The configured publications currently expose no tables.")
            : Finding.Ok("BTS004", $"The configured publications expose {tables.Count} distinct table(s)."));

        var slot = await ReadSlotAsync(source, options.Slot, cancellationToken).ConfigureAwait(false);
        var database = await ScalarStringAsync(source, "SELECT current_database()", cancellationToken)
            .ConfigureAwait(false);
        if (slot is null)
        {
            findings.Add(options.SkipSlot
                ? Finding.Warning("BTS005", $"Logical slot '{options.Slot}' is absent and slot provisioning was skipped.")
                : Finding.Error("BTS005", $"Logical slot '{options.Slot}' does not exist."));
        }
        else if (!string.Equals(slot.Value.Plugin, "pgoutput", StringComparison.Ordinal) ||
                 !string.Equals(slot.Value.Database, database, StringComparison.Ordinal))
        {
            findings.Add(Finding.Error(
                "BTS005",
                $"Logical slot '{options.Slot}' targets database '{slot.Value.Database}' with plug-in '{slot.Value.Plugin}'."));
        }
        else
        {
            findings.Add(Finding.Ok(
                "BTS005",
                $"Logical slot '{options.Slot}' is compatible and {(slot.Value.Active ? "active" : "inactive")}."));
        }

        if (control is null)
        {
            findings.Add(options.DirectOnly
                ? Finding.Ok("BTS006", "Direct slot-per-group mode was selected explicitly.")
                : Finding.Warning("BTS006", "Relay control storage was not inspected; provide --control-connection."));
        }
        else
        {
            var sourceIdentity = await ReadDatabaseIdentityAsync(source, cancellationToken).ConfigureAwait(false);
            var controlIdentity = await ReadDatabaseIdentityAsync(control, cancellationToken).ConfigureAwait(false);
            if (sourceIdentity == controlIdentity && !options.AllowSharedControl)
            {
                findings.Add(Finding.Error(
                    "BTS006",
                    "Relay control storage resolves to the source database without --allow-shared-control."));
            }
            else
            {
                findings.Add(Finding.Ok(
                    "BTS006",
                    sourceIdentity == controlIdentity
                        ? "Shared control storage was explicitly enabled."
                        : "Relay control storage is isolated from the source database."));
            }

            if (sourceIdentity == controlIdentity &&
                tables.Any(table => string.Equals(table.Schema, options.ControlSchema, StringComparison.Ordinal)))
            {
                findings.Add(Finding.Error(
                    "BTS007",
                    $"A publication contains the relay control schema '{options.ControlSchema}'."));
            }
            else
            {
                findings.Add(Finding.Ok("BTS007", "Publications exclude the relay's own control schema."));
            }
        }

        findings.Add(Finding.Ok("BTS008", $"Publication fingerprint: {Fingerprint(options.Publications, tables)}"));
        return findings;
    }

    private static async Task CreatePublicationAsync(
        DbDataSource source,
        string publication,
        StreamsCliOptions options,
        CancellationToken cancellationToken)
    {
        var target = options.AllTables
            ? "FOR ALL TABLES"
            : "FOR TABLE " + string.Join(", ", options.Tables.Select(QuoteQualifiedTable));
        await ExecuteAsync(
            source,
            $"CREATE PUBLICATION {QuoteIdentifier(publication)} {target}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateSlotAsync(
        DbDataSource source,
        string slot,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT slot_name FROM pg_create_logical_replication_slot(@slot, 'pgoutput', false, false)";
        AddParameter(command, "slot", slot);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PublicationExistsAsync(
        DbDataSource source,
        string publication,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = @publication)";
        AddParameter(command, "publication", publication);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<List<PostgreSqlPublishedTable>> ReadPublishedTablesAsync(
        DbDataSource source,
        IReadOnlyList<string> publications,
        CancellationToken cancellationToken)
    {
        var tables = new HashSet<PostgreSqlPublishedTable>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var publication in publications)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT schemaname, tablename FROM pg_publication_tables " +
                "WHERE pubname = @publication ORDER BY schemaname, tablename";
            AddParameter(command, "publication", publication);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = tables.Add(new PostgreSqlPublishedTable(reader.GetString(0), reader.GetString(1)));
            }
        }

        return tables.OrderBy(static table => table.Schema, StringComparer.Ordinal)
            .ThenBy(static table => table.Table, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<SlotState?> ReadSlotAsync(
        DbDataSource source,
        string slot,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT plugin, database, active FROM pg_replication_slots WHERE slot_name = @slot";
        AddParameter(command, "slot", slot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SlotState(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))
            : null;
    }

    private static async Task<DatabaseIdentity> ReadDatabaseIdentityAsync(
        DbDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT current_database(), COALESCE(inet_server_addr()::text, 'local'), current_setting('port')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<string> ScalarStringAsync(
        DbDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
                   await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                   CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static async Task ExecuteAsync(
        DbDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private static StreamsCliOptions ParseOptions(string command, string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var publications = new List<string>();
        var tables = new List<string>();
        var help = false;
        var allTables = false;
        var directOnly = false;
        var allowSharedControl = false;
        var skipSlot = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--all-tables":
                    allTables = true;
                    break;
                case "--direct-only":
                    directOnly = true;
                    break;
                case "--allow-shared-control":
                    allowSharedControl = true;
                    break;
                case "--skip-slot":
                    skipSlot = true;
                    break;
                case "--publication":
                    publications.Add(ReadValue(arguments, ref index, argument));
                    break;
                case "--table":
                    tables.Add(ReadValue(arguments, ref index, argument));
                    break;
                case "--connection" or "--control-connection" or "--control-schema" or "--slot":
                    values[argument] = ReadValue(arguments, ref index, argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown {command} option '{argument}'.");
            }
        }

        if (help)
        {
            return StreamsCliOptions.HelpOnly;
        }

        if (allTables && tables.Count > 0)
        {
            throw new ArgumentException("Use either --all-tables or one or more --table values, not both.");
        }

        foreach (var table in tables)
        {
            _ = ParseQualifiedTable(table);
        }

        var source = values.GetValueOrDefault("--connection")
            ?? Environment.GetEnvironmentVariable(SourceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException(
                $"A source connection is required through --connection or {SourceEnvironmentVariable}.");
        }

        var control = values.GetValueOrDefault("--control-connection")
            ?? Environment.GetEnvironmentVariable(ControlEnvironmentVariable);
        if (command == "provision" && !directOnly && string.IsNullOrWhiteSpace(control))
        {
            throw new ArgumentException(
                $"Provisioning requires a separate relay connection through --control-connection or " +
                $"{ControlEnvironmentVariable}; use --direct-only to opt out.");
        }

        if (publications.Count == 0)
        {
            throw new ArgumentException("At least one --publication value is required.");
        }

        var slot = values.GetValueOrDefault("--slot");
        if (string.IsNullOrWhiteSpace(slot))
        {
            throw new ArgumentException("--slot is required.");
        }

        return new StreamsCliOptions(
            source,
            control,
            values.GetValueOrDefault("--control-schema") ?? "bluetusk_streams",
            slot,
            publications.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            tables.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            allTables,
            directOnly,
            allowSharedControl,
            skipSlot,
            Help: false);
    }

    private static string ReadValue(string[] arguments, ref int index, string option)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return arguments[index];
    }

    private static string QuoteQualifiedTable(string table)
    {
        var parts = ParseQualifiedTable(table);
        return $"{QuoteIdentifier(parts.Schema)}.{QuoteIdentifier(parts.Table)}";
    }

    private static (string Schema, string Table) ParseQualifiedTable(string table)
    {
        var parts = table.Split('.', StringSplitOptions.TrimEntries);
        if (parts is not [var schema, var name] ||
            string.IsNullOrWhiteSpace(schema) ||
            string.IsNullOrWhiteSpace(name) ||
            schema.Contains('\0') ||
            name.Contains('\0'))
        {
            throw new ArgumentException($"Table '{table}' must use the schema.table form.");
        }

        return (schema, name);
    }

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Fingerprint(
        IReadOnlyList<string> publications,
        IReadOnlyList<PostgreSqlPublishedTable> tables)
    {
        var canonical = string.Join('\n', publications.Select(static value => "P:" + value)
            .Concat(tables.Select(static value => $"T:{value.Schema}.{value.Table}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Sanitize(string message, params string?[] secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                message = message.Replace(secret, "<connection string redacted>", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private static void WriteReport(IEnumerable<Finding> findings, TextWriter output)
    {
        foreach (var finding in findings)
        {
            output.WriteLine($"{finding.Severity.ToString().ToUpperInvariant()} {finding.Code} {finding.Message}");
        }
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("BlueTusk Streams PostgreSQL tooling");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  bluetusk-streams validate [options]");
        output.WriteLine("  bluetusk-streams provision [options]");
        output.WriteLine();
        output.WriteLine("Run 'bluetusk-streams <command> --help' for command options.");
    }

    private static void WriteCommandHelp(string command, TextWriter output)
    {
        output.WriteLine($"Usage: bluetusk-streams {command} [options]");
        output.WriteLine();
        output.WriteLine("Source:");
        output.WriteLine($"  --connection <value>          Source connection; or {SourceEnvironmentVariable}.");
        output.WriteLine("  --publication <name>         Publication name; repeatable and required.");
        output.WriteLine("  --slot <name>                Logical replication slot name; required.");
        output.WriteLine("  --skip-slot                  Skip slot creation; validation reports its absence.");
        output.WriteLine();
        output.WriteLine("Relay:");
        output.WriteLine($"  --control-connection <value> Separate control connection; or {ControlEnvironmentVariable}.");
        output.WriteLine("  --control-schema <name>      Relay schema (default: bluetusk_streams).");
        output.WriteLine("  --direct-only                Explicitly provision without durable relay storage.");
        output.WriteLine("  --allow-shared-control       Explicitly allow source and control in one database.");
        if (command == "provision")
        {
            output.WriteLine();
            output.WriteLine("Publication creation:");
            output.WriteLine("  --table <schema.table>       Published table; repeatable.");
            output.WriteLine("  --all-tables                 Publish every source table (not valid for shared control).");
        }
    }

    private enum FindingSeverity
    {
        Ok,
        Warning,
        Error,
    }

    private sealed record Finding(FindingSeverity Severity, string Code, string Message)
    {
        public static Finding Ok(string code, string message) => new(FindingSeverity.Ok, code, message);

        public static Finding Warning(string code, string message) => new(FindingSeverity.Warning, code, message);

        public static Finding Error(string code, string message) => new(FindingSeverity.Error, code, message);
    }

    private readonly record struct SlotState(string Plugin, string Database, bool Active);

    private readonly record struct DatabaseIdentity(string Database, string Address, string Port);

    private sealed record StreamsCliOptions(
        string SourceConnection,
        string? ControlConnection,
        string ControlSchema,
        string Slot,
        IReadOnlyList<string> Publications,
        IReadOnlyList<string> Tables,
        bool AllTables,
        bool DirectOnly,
        bool AllowSharedControl,
        bool SkipSlot,
        bool Help)
    {
        public static StreamsCliOptions HelpOnly { get; } = new(
            "",
            null,
            "bluetusk_streams",
            "",
            [],
            [],
            AllTables: false,
            DirectOnly: false,
            AllowSharedControl: false,
            SkipSlot: false,
            Help: true);
    }
}
