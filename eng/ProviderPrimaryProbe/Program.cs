using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlueTusk.Benchmarks;
using BlueTusk.Data;

const int concurrency = 64;
const string feature = "parameterized-scalar";
const int poolSize = 2 * concurrency + 2;
if (args is not [var outputPath, var commit, var role, var trial, var warmup, var duration, var samples, var evidenceClass] ||
    role is not ("candidate" or "reference") || evidenceClass is not ("qualification" or "diagnostic"))
{
    throw new ArgumentException("The Provider probe requires a raw output path and the matched trial identity.");
}

var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
    throw new PlatformNotSupportedException("The Provider primary comparison requires Windows or Linux.");
if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    throw new PlatformNotSupportedException("The Provider primary comparison requires x64.");
}

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_CONNECTION_STRING");
ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
var warmupSeconds = int.Parse(warmup, CultureInfo.InvariantCulture);
var measurementSeconds = int.Parse(duration, CultureInfo.InvariantCulture);
var maximumSamplesPerWorker = int.Parse(samples, CultureInfo.InvariantCulture);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180 + warmupSeconds + measurementSeconds));
var token = deadline.Token;
var settings = new DbConnectionStringBuilder { ConnectionString = connectionString };
settings["Pooling"] = true;
settings["Multiplexing"] = false;
settings["Minimum Pool Size"] = 0;
settings["Maximum Pool Size"] = poolSize;
await using var dataSource = new BlueTuskDataSourceBuilder(settings.ConnectionString).Build();
var connections = new List<BlueTuskConnection>(concurrency);
try
{
    for (var worker = 0; worker < concurrency; worker++)
    {
        connections.Add((BlueTuskConnection)await dataSource.OpenConnectionAsync(token));
    }

    await using (var check = (BlueTuskCommand)connections[0].CreateCommand())
    {
        check.CommandText = "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()";
        if (await check.ExecuteScalarAsync<bool>(token))
        {
            throw new InvalidOperationException("The approved primary Provider workload uses the native plaintext fixture.");
        }
    }

    var workload = connections.Select<BlueTuskConnection, Func<CancellationToken, Task>>(
        connection => cancellation => QueryAsync(connection, cancellation)).ToArray();
    _ = await ProviderRequestCapture.MeasureWindowAsync(workload, warmupSeconds, 0, token);
    var measured = await ProviderRequestCapture.MeasureWindowAsync(workload, measurementSeconds, maximumSamplesPerWorker, token);
    var implementation = typeof(BlueTuskConnection).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    var report = new
    {
        SchemaVersion = 1,
        EvidenceKind = "bluetusk-performance-trial",
        Family = "Provider",
        WorkloadKey = $"{os}|Provider|primary-hot-path",
        Role = role,
        Trial = int.Parse(trial, CultureInfo.InvariantCulture),
        SourceCommit = commit,
        Diagnostic = evidenceClass == "diagnostic",
        Synthetic = false,
        Implementation = implementation,
        WarmupSeconds = warmupSeconds,
        MeasurementSeconds = measurementSeconds,
        Environment = new
        {
            Os = os,
            Architecture = "x64",
            Runtime = RuntimeInformation.FrameworkDescription,
            Feature = feature,
            Concurrency = concurrency,
            Variant = "native",
            PoolSize = poolSize,
            TlsActive = false,
            ServerVersion = connections[0].ServerVersion,
        },
        Stopwatch.Frequency,
        Measurement = measured with
        {
            Workers = measured.Workers.Select(worker => new ProviderRequestCapture.WorkerSamples(
                worker.RequestTicks.AsSpan(0, worker.Count).ToArray(), worker.Count)).ToArray(),
        },
    };
    await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
}
finally
{
    foreach (var connection in connections) await connection.DisposeAsync();
}

static async Task QueryAsync(BlueTuskConnection connection, CancellationToken cancellationToken)
{
    await using var command = (BlueTuskCommand)connection.CreateCommand();
    command.CommandText = "SELECT $1::int4 + 1";
    command.Parameters.Add(new BlueTuskParameter<int>(41));
    if (await command.ExecuteScalarAsync<int>(cancellationToken) != 42)
    {
        throw new InvalidDataException("The primary Provider query returned an unexpected value.");
    }
}
