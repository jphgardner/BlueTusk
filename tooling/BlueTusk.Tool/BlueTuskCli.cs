using System.Reflection;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable EF1001 // The provider CLI composes its own design-time service implementation.

namespace BlueTusk.Tool;

/// <summary>Command-line entry point for BlueTusk design-time tooling.</summary>
public static class BlueTuskCli
{
    private const string ConnectionEnvironmentVariable = "BLUETUSK_CONNECTION_STRING";

    /// <summary>Runs the BlueTusk command line.</summary>
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

        if (arguments is ["--version"] or ["-v"])
        {
            output.WriteLine(
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
            return 0;
        }

        if (string.Equals(arguments[0], "doctor", StringComparison.Ordinal))
        {
            DoctorOptions doctorOptions;
            try
            {
                doctorOptions = ParseDoctorOptions(arguments.Skip(1).ToArray());
            }
            catch (ArgumentException exception)
            {
                error.WriteLine(exception.Message);
                error.WriteLine("Run 'bluetusk doctor --help' for usage.");
                return 2;
            }

            if (doctorOptions.Help)
            {
                WriteDoctorHelp(output);
                return 0;
            }

            return BlueTuskDoctor.RunAsync(doctorOptions, output, error)
                .GetAwaiter()
                .GetResult();
        }

        if (!string.Equals(arguments[0], "scaffold", StringComparison.Ordinal))
        {
            error.WriteLine($"Unknown BlueTusk command '{arguments[0]}'.");
            error.WriteLine("Run 'bluetusk --help' for usage.");
            return 2;
        }

        ScaffoldOptions options;
        try
        {
            options = ParseScaffoldOptions(arguments.Skip(1).ToArray());
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine("Run 'bluetusk scaffold --help' for usage.");
            return 2;
        }

        if (options.Help)
        {
            WriteScaffoldHelp(output);
            return 0;
        }

        try
        {
            Scaffold(options, output);
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"BlueTusk scaffolding failed: {Sanitize(exception.Message, options.ConnectionString)}");
            return 1;
        }
    }

    private static void Scaffold(ScaffoldOptions options, TextWriter output)
    {
        var projectDirectory = Path.GetFullPath(options.ProjectDirectory);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory, projectDirectory);
        var connectionForGeneratedCode = options.IncludeConnectionString
            ? options.ConnectionString
            : "";

        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var scaffolder = provider.GetRequiredService<IReverseEngineerScaffolder>();
        var scaffolded = scaffolder.ScaffoldModel(
            options.ConnectionString,
            new DatabaseModelFactoryOptions(options.Tables, options.Schemas),
            new ModelReverseEngineerOptions
            {
                UseDatabaseNames = options.UseDatabaseNames,
                NoPluralize = options.NoPluralize,
            },
            new ModelCodeGenerationOptions
            {
                ContextName = options.ContextName,
                ConnectionString = connectionForGeneratedCode,
                ContextNamespace = options.ContextNamespace,
                ModelNamespace = options.ModelNamespace,
                RootNamespace = options.RootNamespace,
                Language = "C#",
                ProjectDir = projectDirectory,
                UseDataAnnotations = options.UseDataAnnotations,
                UseNullableReferenceTypes = true,
                SuppressOnConfiguring = !options.IncludeConnectionString,
                SuppressConnectionStringWarning = false,
            });

        _ = scaffolder.Save(scaffolded, outputDirectory, options.Force);
        output.WriteLine(
            $"Scaffolded {scaffolded.AdditionalFiles.Count + 1} file(s) to '{outputDirectory}'.");
    }

    private static ScaffoldOptions ParseScaffoldOptions(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var schemas = new List<string>();
        var tables = new List<string>();
        var force = false;
        var includeConnectionString = false;
        var useDataAnnotations = false;
        var useDatabaseNames = false;
        var noPluralize = false;
        var help = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--include-connection-string":
                    includeConnectionString = true;
                    break;
                case "--data-annotations":
                    useDataAnnotations = true;
                    break;
                case "--use-database-names":
                    useDatabaseNames = true;
                    break;
                case "--no-pluralize":
                    noPluralize = true;
                    break;
                case "--include-graphs" or "--include-functions" or "--include-views":
                    // BlueTusk retains all PostgreSQL metadata by default. These product-spec aliases
                    // remain accepted so scripts can state their intent explicitly.
                    break;
                case "--schema":
                    schemas.Add(ReadValue(arguments, ref index, argument));
                    break;
                case "--table":
                    tables.Add(ReadValue(arguments, ref index, argument));
                    break;
                case "--connection" or "--output" or "--context" or "--namespace" or
                    "--context-namespace" or "--root-namespace" or "--project-dir":
                    values[argument] = ReadValue(arguments, ref index, argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown scaffold option '{argument}'.");
            }
        }

        if (help)
        {
            return ScaffoldOptions.HelpOnly;
        }

        var connectionString = values.GetValueOrDefault("--connection")
            ?? Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                $"A connection string is required through --connection or {ConnectionEnvironmentVariable}.");
        }

        var modelNamespace = values.GetValueOrDefault("--namespace") ?? "BlueTusk.Models";
        return new ScaffoldOptions(
            connectionString,
            values.GetValueOrDefault("--output") ?? "Models",
            values.GetValueOrDefault("--context") ?? "BlueTuskContext",
            modelNamespace,
            values.GetValueOrDefault("--context-namespace") ?? modelNamespace,
            values.GetValueOrDefault("--root-namespace") ?? modelNamespace,
            values.GetValueOrDefault("--project-dir") ?? Directory.GetCurrentDirectory(),
            schemas,
            tables,
            force,
            includeConnectionString,
            useDataAnnotations,
            useDatabaseNames,
            noPluralize,
            Help: false);
    }

    private static DoctorOptions ParseDoctorOptions(string[] arguments)
    {
        string? connectionString = null;
        var extensions = new List<string>();
        var requireStreams = false;
        var requireTls = false;
        var json = false;
        var help = false;
        var timeoutSeconds = 10;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--require-streams":
                    requireStreams = true;
                    break;
                case "--require-tls":
                    requireTls = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--connection":
                    connectionString = ReadValue(arguments, ref index, argument);
                    break;
                case "--extension":
                    extensions.Add(ReadValue(arguments, ref index, argument));
                    break;
                case "--timeout":
                    var value = ReadValue(arguments, ref index, argument);
                    if (!int.TryParse(value, out timeoutSeconds) || timeoutSeconds is < 1 or > 120)
                    {
                        throw new ArgumentException("Option '--timeout' must be an integer from 1 to 120 seconds.");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown doctor option '{argument}'.");
            }
        }

        if (help)
        {
            return DoctorOptions.HelpOnly;
        }

        connectionString ??= Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                $"A connection string is required through --connection or {ConnectionEnvironmentVariable}.");
        }

        return new DoctorOptions(
            connectionString,
            timeoutSeconds,
            requireStreams,
            requireTls,
            json,
            extensions.Distinct(StringComparer.Ordinal).ToArray(),
            Help: false);
    }

    private static string ReadValue(
        string[] arguments,
        ref int index,
        string option)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return arguments[index];
    }

    private static string Sanitize(string message, string connectionString) =>
        string.IsNullOrEmpty(connectionString)
            ? message
            : message.Replace(connectionString, "<connection string redacted>", StringComparison.Ordinal);

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("BlueTusk PostgreSQL tooling");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  bluetusk doctor [options]");
        output.WriteLine("  bluetusk scaffold [options]");
        output.WriteLine("  bluetusk --version");
        output.WriteLine();
        output.WriteLine("Run 'bluetusk doctor --help' or 'bluetusk scaffold --help' for command options.");
    }

    private static void WriteDoctorHelp(TextWriter output)
    {
        output.WriteLine("Usage: bluetusk doctor [options]");
        output.WriteLine();
        output.WriteLine("Connection:");
        output.WriteLine("  --connection <value>          PostgreSQL connection string; alternatively set");
        output.WriteLine($"                                {ConnectionEnvironmentVariable}.");
        output.WriteLine("  --timeout <seconds>           Overall diagnostic timeout, 1-120 (default: 10).");
        output.WriteLine();
        output.WriteLine("Production requirements:");
        output.WriteLine("  --require-tls                 Fail when the current PostgreSQL session is not encrypted.");
        output.WriteLine("  --require-streams             Require logical WAL and available replication settings.");
        output.WriteLine("  --extension <name>            Require an installed extension; repeatable.");
        output.WriteLine("  --json                        Write a versioned machine-readable report.");
        output.WriteLine();
        output.WriteLine("The command is read-only and never prints the supplied connection string.");
    }

    private static void WriteScaffoldHelp(TextWriter output)
    {
        output.WriteLine("Usage: bluetusk scaffold [options]");
        output.WriteLine();
        output.WriteLine("Required:");
        output.WriteLine("  --connection <value>          PostgreSQL connection string; alternatively set");
        output.WriteLine($"                                {ConnectionEnvironmentVariable}.");
        output.WriteLine();
        output.WriteLine("Selection:");
        output.WriteLine("  --schema <name>               Include a schema; repeatable.");
        output.WriteLine("  --table <schema.table>        Include a table; repeatable.");
        output.WriteLine("  --include-graphs              Explicit alias; graphs are included by default.");
        output.WriteLine("  --include-functions           Explicit alias; routines are included by default.");
        output.WriteLine("  --include-views               Explicit alias; views are included by default.");
        output.WriteLine();
        output.WriteLine("Generation:");
        output.WriteLine("  --output <directory>          Output directory (default: Models).");
        output.WriteLine("  --project-dir <directory>     Base project directory (default: current directory).");
        output.WriteLine("  --context <name>              DbContext name (default: BlueTuskContext).");
        output.WriteLine("  --namespace <name>            Entity namespace (default: BlueTusk.Models).");
        output.WriteLine("  --context-namespace <name>    DbContext namespace.");
        output.WriteLine("  --root-namespace <name>       Project root namespace.");
        output.WriteLine("  --data-annotations            Prefer supported data annotations.");
        output.WriteLine("  --use-database-names          Preserve database identifiers in generated names.");
        output.WriteLine("  --no-pluralize                Disable pluralization.");
        output.WriteLine("  --include-connection-string   Generate OnConfiguring with the supplied connection string.");
        output.WriteLine("  --force                       Overwrite generated files that already exist.");
    }

    private sealed record ScaffoldOptions(
        string ConnectionString,
        string OutputDirectory,
        string ContextName,
        string ModelNamespace,
        string ContextNamespace,
        string RootNamespace,
        string ProjectDirectory,
        IReadOnlyList<string> Schemas,
        IReadOnlyList<string> Tables,
        bool Force,
        bool IncludeConnectionString,
        bool UseDataAnnotations,
        bool UseDatabaseNames,
        bool NoPluralize,
        bool Help)
    {
        public static ScaffoldOptions HelpOnly { get; } = new(
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            [],
            [],
            Force: false,
            IncludeConnectionString: false,
            UseDataAnnotations: false,
            UseDatabaseNames: false,
            NoPluralize: false,
            Help: true);
    }
}

internal sealed record DoctorOptions(
    string ConnectionString,
    int TimeoutSeconds,
    bool RequireStreams,
    bool RequireTls,
    bool Json,
    IReadOnlyList<string> RequiredExtensions,
    bool Help)
{
    public static DoctorOptions HelpOnly { get; } = new(
        "",
        10,
        RequireStreams: false,
        RequireTls: false,
        Json: false,
        RequiredExtensions: [],
        Help: true);
}

#pragma warning restore EF1001
