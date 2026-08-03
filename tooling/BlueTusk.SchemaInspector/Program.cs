using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Data.Schema;

var (options, parseError) = SchemaInspectorOptions.Parse(args);
if (options is null)
{
    if (parseError is not null)
    {
        Console.Error.WriteLine(parseError);
    }

    WriteUsage(parseError is null ? Console.Out : Console.Error);
    return parseError is null ? 0 : 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await using var dataSource = BlueTuskDataSource.Create(options.ConnectionString);
    await using var connection = await dataSource.OpenConnectionAsync(cancellation.Token);
    var capabilities = connection.ServerCapabilities ??
        throw new InvalidOperationException("The open PostgreSQL session did not report server capabilities.");
    var inspector = new BlueTuskPropertyGraphSchemaInspector(connection);
    var graphs = await inspector.InspectAsync(
        new BlueTuskPropertyGraphInspectionOptions
        {
            Catalog = options.Catalog,
            Schema = options.Schema,
            Name = options.Graph,
        },
        cancellation.Token);

    if (options.Json)
    {
        WriteJson(capabilities, graphs);
    }
    else
    {
        WriteText(capabilities, graphs);
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Property-graph inspection was cancelled.");
    return 130;
}
catch (DbException exception)
{
    Console.Error.WriteLine($"Could not inspect PostgreSQL schema: {exception.Message}");
    return 1;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Could not inspect PostgreSQL schema: {exception.Message}");
    return 1;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine($"Could not inspect PostgreSQL schema: {exception.Message}");
    return 1;
}

static void WriteText(
    BlueTuskServerCapabilities capabilities,
    IReadOnlyList<BlueTuskPropertyGraphSchema> graphs)
{
    Console.WriteLine($"PostgreSQL {capabilities.ServerVersion}: SQL/PGQ property-graph schema");
    if (!capabilities.SupportsSqlPgq)
    {
        Console.WriteLine("Property-graph discovery is unavailable on this server.");
        return;
    }

    if (graphs.Count == 0)
    {
        Console.WriteLine("No matching property graphs were found.");
        return;
    }

    foreach (var graph in graphs)
    {
        Console.WriteLine($"Property graph {FormatName(graph.Name)}");
        foreach (var element in graph.ElementTables)
        {
            Console.WriteLine(
                $"  {element.Kind.ToString().ToUpperInvariant()} {element.Alias} -> {FormatName(element.Table)}");
            if (element.KeyColumns.Count > 0)
            {
                Console.WriteLine($"    key: {string.Join(", ", element.KeyColumns.Select(column => column.Name))}");
            }

            if (element.Labels.Count > 0)
            {
                Console.WriteLine($"    labels: {string.Join(", ", element.Labels)}");
            }

            foreach (var property in element.Properties)
            {
                var dataType = graph.PropertyDataTypes.FirstOrDefault(
                    candidate => string.Equals(candidate.PropertyName, property.Name, StringComparison.Ordinal));
                var typeSuffix = dataType is null ? string.Empty : $" ({FormatDataType(dataType)})";
                Console.WriteLine($"    property {property.Name} = {property.Expression}{typeSuffix}");
            }

            foreach (var endpoint in element.Endpoints)
            {
                var mappings = string.Join(
                    ", ",
                    endpoint.Columns.Select(mapping =>
                        $"{mapping.EdgeTableColumn} -> {mapping.VertexTableColumn}"));
                Console.WriteLine(
                    $"    {endpoint.End.ToString().ToUpperInvariant()} -> {endpoint.VertexTableAlias} ({mappings})");
            }
        }

        foreach (var label in graph.Labels)
        {
            Console.WriteLine($"  LABEL {label.Name}: {string.Join(", ", label.Properties)}");
        }
    }
}

static void WriteJson(
    BlueTuskServerCapabilities capabilities,
    IReadOnlyList<BlueTuskPropertyGraphSchema> graphs)
{
    var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    serializerOptions.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                serverVersion = capabilities.ServerVersion.ToString(),
                capabilities.SupportsSqlPgq,
                graphs,
            },
            serializerOptions));
}

static string FormatName(BlueTuskSchemaObjectName name) =>
    $"{name.Catalog}.{name.Schema}.{name.Name}";

static string FormatDataType(BlueTuskPropertyGraphPropertyDataType dataType) =>
    dataType.DataType == "USER-DEFINED" && dataType.UserDefinedTypeName is not null
        ? $"{dataType.UserDefinedTypeSchema}.{dataType.UserDefinedTypeName}"
        : dataType.DataType;

static void WriteUsage(TextWriter output)
{
    output.WriteLine(
        "Usage: BlueTusk.SchemaInspector [--connection <connection-string>] " +
        "[--catalog <catalog>] [--schema <schema>] [--graph <name>] [--json]");
    output.WriteLine(
        "Uses BLUETUSK_CONNECTION_STRING when --connection is omitted. " +
        "PostgreSQL 19 property graphs are discovered through information_schema.");
}

internal sealed record SchemaInspectorOptions(
    string ConnectionString,
    string? Catalog,
    string? Schema,
    string? Graph,
    bool Json)
{
    public static (SchemaInspectorOptions? Options, string? Error) Parse(string[] arguments)
    {
        if (arguments is ["--help"] or ["-h"])
        {
            return (null, null);
        }

        string? connectionString = null;
        string? catalog = null;
        string? schema = null;
        string? graph = null;
        var json = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--connection":
                    if (!TryReadValue(arguments, ref index, out connectionString))
                    {
                        return (null, "--connection requires a value.");
                    }

                    break;
                case "--catalog":
                    if (!TryReadValue(arguments, ref index, out catalog))
                    {
                        return (null, "--catalog requires a value.");
                    }

                    break;
                case "--schema":
                    if (!TryReadValue(arguments, ref index, out schema))
                    {
                        return (null, "--schema requires a value.");
                    }

                    break;
                case "--graph":
                    if (!TryReadValue(arguments, ref index, out graph))
                    {
                        return (null, "--graph requires a value.");
                    }

                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                    return (null, "--help cannot be combined with other arguments.");
                default:
                    return (null, $"Unknown argument '{arguments[index]}'.");
            }
        }

        connectionString ??= Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return (
                null,
                "A connection string is required through --connection or BLUETUSK_CONNECTION_STRING.");
        }

        return (new SchemaInspectorOptions(connectionString, catalog, schema, graph, json), null);
    }

    private static bool TryReadValue(string[] arguments, ref int index, out string? value)
    {
        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = arguments[++index];
        return true;
    }
}
