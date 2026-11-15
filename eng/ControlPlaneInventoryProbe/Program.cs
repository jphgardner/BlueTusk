using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlueTusk.ControlPlane;
using BlueTusk.Dashboard;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;

const int sourceCount = 100;
const int clientCount = 32;
const string schema = "control_plane_inventory_probe";
const string endpoint = "bluetusk/api/overview";

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_INVENTORY_PROBE_CONNECTION");
ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
await using var dataSource = BlueTuskDataSource.Create(connectionString);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = deadline.Token;

if (args is ["seed"])
{
    var relay = new PostgreSqlDurableChangeRelay(new PostgreSqlStreamsStorageOptions
    {
        ControlDataSource = dataSource,
        ControlSchema = schema,
    });
    await relay.InitializeAsync(token);
    for (var i = 0; i < sourceCount; i++)
    {
        var slot = "inventory_probe_" + i.ToString(CultureInfo.InvariantCulture);
        await relay.RegisterSourceAsync(
            new ChangeSourceIdentity("inventory-probe", "bluetusk_benchmark", slot, "inventory-probe"),
            cancellationToken: token);
    }

    return;
}

if (args is not [var outputPath, var role, var commit, var trialNumber, var warmup, var duration])
{
    throw new ArgumentException("Select seed or specify an output path, role, commit, trial number, warmup seconds and measurement seconds.");
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
builder.Services.AddAuthorizationBuilder().AddPolicy("probe-read", policy => policy.RequireAssertion(_ => true));
builder.Services.AddSingleton<IControlPlaneQueryService>(new PostgreSqlControlPlaneQueryService(
    [new ControlPlanePostgreSqlSource("probe", dataSource, dataSource, schema)]));
// The 1.0 Dashboard declares these service parameters while building routes.
// The only exercised endpoint is the real inventory read.
builder.Services.AddSingleton<IControlPlaneSyncQueryService>(_ => throw UnusedRoute());
builder.Services.AddSingleton<IControlPlaneLiveQueryService>(_ => throw UnusedRoute());
builder.Services.AddSingleton<IControlPlaneContinuousGraphQueryService>(_ => throw UnusedRoute());
builder.Services.AddSingleton<ControlPlaneOperationExecutor>(_ => throw UnusedRoute());
await using var app = builder.Build();
app.MapBlueTuskDashboard(options => options.ReadAuthorizationPolicy = "probe-read");
await app.StartAsync(token);

using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = clientCount })
{
    BaseAddress = new Uri(app.Urls.Single() + "/"),
};
var warmupSeconds = int.Parse(warmup, CultureInfo.InvariantCulture);
var measurementSeconds = int.Parse(duration, CultureInfo.InvariantCulture);
await CaptureAsync(http, warmupSeconds, false, token);

using var process = Process.GetCurrentProcess();
var gc = Collections();
var allocated = GC.GetTotalAllocatedBytes(precise: true);
var cpu = process.TotalProcessorTime;
var start = Stopwatch.GetTimestamp();
var workers = await CaptureAsync(http, measurementSeconds, true, token);
var elapsed = Stopwatch.GetTimestamp() - start;
process.Refresh();
var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
    throw new PlatformNotSupportedException("Use Windows or Linux.");
var implementation = typeof(PostgreSqlControlPlaneQueryService).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
var result = new
{
    SchemaVersion = 1,
    EvidenceKind = "bluetusk-performance-trial",
    Family = "ControlPlane",
    WorkloadKey = $"{os}|ControlPlane|sources={sourceCount}|clients={clientCount}",
    Role = role,
    SourceCommit = commit,
    Trial = int.Parse(trialNumber, CultureInfo.InvariantCulture),
    Diagnostic = true,
    Synthetic = false,
    Implementation = implementation,
    Environment = new
    {
        Os = os,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        Runtime = RuntimeInformation.FrameworkDescription,
        Scope = "Kestrel API and HTTP clients in one process",
    },
    Frequency = Stopwatch.Frequency,
    Measurement = new
    {
        ElapsedTicks = elapsed,
        CompletedOperations = workers.Sum(worker => (long)worker.Count),
        AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
        CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
        PeakWorkingSetBytes = process.PeakWorkingSet64,
        GcCollections = Collections().Select((count, generation) => count - gc[generation]).ToArray(),
        Workers = workers.Select(ticks => new { RequestTicks = ticks, ticks.Count }).ToArray(),
    },
};
await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
await JsonSerializer.SerializeAsync(output, result, JsonSerializerOptions.Web, token);

static int[] Collections() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

static InvalidOperationException UnusedRoute() =>
    new("The probe only serves inventory requests.");

static Task<List<long>[]> CaptureAsync(HttpClient http, int seconds, bool measure, CancellationToken token)
{
    var finish = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
    return Task.WhenAll(Enumerable.Range(0, clientCount).Select(_ => WorkerAsync()));

    async Task<List<long>> WorkerAsync()
    {
        var samples = new List<long>();
        do
        {
            var started = Stopwatch.GetTimestamp();
            var payload = await http.GetByteArrayAsync(endpoint, token);
            var elapsed = Stopwatch.GetTimestamp() - started;
            using var inventory = JsonDocument.Parse(payload);
            if (inventory.RootElement.GetProperty("sources").GetArrayLength() != sourceCount)
            {
                throw new InvalidOperationException("The overview did not return all registered sources.");
            }

            if (measure)
            {
                samples.Add(elapsed);
            }
        }
        while (Stopwatch.GetTimestamp() < finish);

        return samples;
    }
}
