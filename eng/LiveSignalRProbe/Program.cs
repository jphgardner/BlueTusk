using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using BlueTusk.Benchmarks;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Live.SignalR;
using BlueTusk.LiveSignalRProbe;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

if (args is not [var outputPath, var commit, var role, var trial, var results, var subscribers, var warmup, var duration] ||
    role is not ("candidate" or "reference"))
{
    throw new ArgumentException("Provide output, commit, role, trial, results, subscribers, warmup and measurement seconds.");
}

var candidate = role == "candidate";
var resultCount = int.Parse(results, CultureInfo.InvariantCulture);
var subscriberCount = int.Parse(subscribers, CultureInfo.InvariantCulture);
var trialIndex = int.Parse(trial, CultureInfo.InvariantCulture);
var warmupSeconds = int.Parse(warmup, CultureInfo.InvariantCulture);
var measurementSeconds = int.Parse(duration, CultureInfo.InvariantCulture);
if (resultCount is not (10 or 1000 or 100000) || subscriberCount is not (1 or 64 or 1000 or 10000) ||
    trialIndex is < 0 or > 49 || warmupSeconds is < 1 or > 60 || measurementSeconds is < 1 or > 60)
{
    throw new ArgumentException("The capture requires a defined result and subscriber count, a trial and bounded timing.");
}

var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
    throw new PlatformNotSupportedException("The Live SignalR comparison requires Windows or Linux.");
if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    throw new PlatformNotSupportedException("The Live SignalR comparison requires x64.");
}

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_CONNECTION_STRING");
ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var token = deadline.Token;
await using var source = BlueTuskDataSource.Create(connectionString);
await using var writer = (BlueTuskConnection)await source.OpenConnectionAsync(token);
await using var reader = (BlueTuskConnection)await source.OpenConnectionAsync(token);

// The launcher supplies a disposable database. Both roles use the same source writer and result query.
await using (var initialize = (BlueTuskCommand)writer.CreateCommand())
{
    initialize.CommandText = """
        DROP PUBLICATION IF EXISTS live_signalr_benchmark;
        DROP SCHEMA IF EXISTS live_signalr_storage CASCADE;
        DROP TABLE IF EXISTS public.live_signalr_results;
        CREATE TABLE public.live_signalr_results (id int PRIMARY KEY, revision int NOT NULL);
        CREATE PUBLICATION live_signalr_benchmark FOR TABLE public.live_signalr_results;
        """;
    _ = await initialize.ExecuteNonQueryAsync(token);
    initialize.CommandText = "INSERT INTO public.live_signalr_results SELECT id, 0 FROM generate_series(1,$1) AS seed(id)";
    initialize.Parameters.Add(new BlueTuskParameter<int>(resultCount));
    _ = await initialize.ExecuteNonQueryAsync(token);
}

await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString, token);
var server = await replication.IdentifySystemAsync(token);
var database = server.DatabaseName ?? throw new InvalidOperationException("The Live fixture has no database identity.");
const string publication = "live_signalr_benchmark";
_ = await replication.CreateReplicationSlotAsync(publication, temporary: true, cancellationToken: token);
var envelopes = replication.StartReplicationAsync(new BlueTuskPgOutputReplicationOptions
{
    SlotName = publication,
    PublicationNames = [publication],
}, token).DecodePgOutputAsync(cancellationToken: token);
var stream = new PgOutputChangeStream(envelopes, new ChangeSourceIdentity(server.SystemIdentifier, database, publication, publication));
await using var deliveries = stream.ReadTransactionsAsync(token).GetAsyncEnumerator(token);

var plan = new LiveQueryPlan<ResultRow, int>(
    SignalRCapture.QueryName, database, new string('a', 64),
    LiveQueryCapabilities.SingleTable | LiveQueryCapabilities.ParameterizedPredicate |
    LiveQueryCapabilities.DeterministicOrdering | LiveQueryCapabilities.BoundedTake,
    [new LiveTableDependency("public", "live_signalr_results")], [], resultCount,
    (_, cancellationToken) => ReadResultAsync(cancellationToken), static row => row.Id);
var arguments = plan.Bind(new Dictionary<string, object?>());
var scope = new LiveSecurityScope("benchmark", "v1");
var identity = LiveSubscriptionIdentity.Create(plan, arguments, scope);
var protector = new LiveResumeTokenProtector([new LiveResumeTokenKey("benchmark", RandomNumberGenerator.GetBytes(32), isPrimary: true)]);
var store = new PostgreSqlLiveInvalidationStore(new() { ControlDataSource = source, ControlSchema = "live_signalr_storage" });
var invalidations = candidate ? new LiveInvalidationConsumer(database, store) : null;
await using var query = candidate ? new LiveQuerySession<ResultRow, int>(plan, arguments, scope, store) : null;
await using var shared = query is null ? null : new LiveSharedSubscription<ResultRow, int>(query, store, new()
{
    MaximumSubscribers = subscriberCount,
    SubscriberBufferCapacity = 1,
});
var reference = candidate ? null : new ReferenceResultPublisher(identity, protector, await ReadResultAsync(token));
if (shared is not null)
{
    await shared.StartAsync(token);
    if (shared.Status.QuerySession.ResultCount != resultCount || shared.Status.PersistedSequence != 1)
    {
        throw new InvalidDataException("The candidate did not persist the fixture's initial result.");
    }
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
builder.Services.AddAuthentication(SignalRCapture.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>(SignalRCapture.AuthenticationScheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
if (shared is not null)
{
    builder.Services.AddSingleton<ILiveTransportSubscriptionResolver>(new FixtureResolver(shared));
    builder.Services.AddBlueTuskLiveAspNetCore(protector);
}
else
{
    builder.Services.AddSingleton(reference!);
}
await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
if (shared is not null)
{
    app.MapBlueTuskLiveHub(SignalRCapture.HubPath);
}
else
{
    app.MapHub<ReferenceSignalRHub>(SignalRCapture.HubPath);
}
await app.StartAsync(token);

var address = new Uri(new Uri(app.Urls.Single()), SignalRCapture.HubPath);
var request = new LiveSubscriptionRequest
{
    Query = SignalRCapture.QueryName,
    Parameters = JsonSerializer.SerializeToElement(new { }),
    ResumeToken = protector.Protect(identity, 1, TimeSpan.FromMinutes(30)),
};
var connections = new HubConnection?[subscriberCount];
var clients = new ChannelReader<JsonElement>[subscriberCount];
var revision = 0;
long observedDeliveries = 0;
try
{
    await Parallel.ForEachAsync(Enumerable.Range(0, subscriberCount),
        new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = token }, async (index, stop) =>
        {
            var connection = new HubConnectionBuilder().WithUrl(address, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            }).Build();
            connections[index] = connection;
            await connection.StartAsync(stop);
            clients[index] = await connection.StreamAsChannelAsync<JsonElement>("SubscribeAsync", request, token);
        });

    while ((shared?.Status.SubscriberCount ?? reference!.SubscriberCount) != subscriberCount)
    {
        var closed = clients.FirstOrDefault(client => client.Completion.IsCompleted);
        if (closed is not null)
        {
            await closed.Completion;
            throw new EndOfStreamException("A SignalR subscription closed before the measurement started.");
        }
        await Task.Delay(10, token);
    }

    _ = await ProviderRequestCapture.MeasureWindowAsync([UpdateAndDeliverAsync], warmupSeconds, 0, token);
    var before = shared?.Status;
    var deliveriesBefore = observedDeliveries;
    var window = await ProviderRequestCapture.MeasureWindowAsync([UpdateAndDeliverAsync], measurementSeconds, 262_144, token);
    var after = shared?.Status;
    var verifiedDeliveries = observedDeliveries - deliveriesBefore;
    if (verifiedDeliveries != checked(window.CompletedOperations * subscriberCount))
    {
        throw new InvalidDataException("The clients did not observe every timed delivery.");
    }
    if (before is not null && after is not null &&
        (after.SubscriberCount != subscriberCount || after.QuerySession.ResultCount != resultCount ||
         after.PersistedSequence - before.PersistedSequence != window.CompletedOperations ||
         after.FanOutDeliveries - before.FanOutDeliveries != verifiedDeliveries))
    {
        throw new InvalidDataException("The candidate did not persist and deliver every measured update.");
    }

    var assembly = candidate ? typeof(LiveQuerySession<ResultRow, int>).Assembly : typeof(Hub).Assembly;
    var implementation = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    var report = new
    {
        SchemaVersion = 1,
        EvidenceKind = "bluetusk-performance-trial",
        Family = "Live",
        WorkloadKey = $"{os}|Live|results={resultCount}|subscribers={subscriberCount}|scenario=affected-key-update",
        Role = role,
        Trial = trialIndex,
        SourceCommit = commit,
        Diagnostic = true,
        Synthetic = false,
        Implementation = candidate ? implementation : $"ASP.NET Core SignalR {implementation}",
        WarmupSeconds = warmupSeconds,
        MeasurementSeconds = measurementSeconds,
        Environment = new
        {
            Os = os,
            Architecture = "x64",
            Runtime = RuntimeInformation.FrameworkDescription,
            Transport = "signalr-json-websockets",
            Scenario = "affected-key-update",
            ResultCount = resultCount,
            SubscriberCount = subscriberCount,
            Source = "postgresql-pgoutput",
            ServerVersion = reader.ServerVersion,
        },
        VerifiedDeliveries = verifiedDeliveries,
        PersistedUpdates = before is not null && after is not null ? after.PersistedSequence - before.PersistedSequence : (long?)null,
        Stopwatch.Frequency,
        Measurement = window with
        {
            Workers = window.Workers.Select(worker => new ProviderRequestCapture.WorkerSamples(
                worker.RequestTicks.AsSpan(0, worker.Count).ToArray(), worker.Count)).ToArray(),
        },
    };
    await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
}
finally
{
    await deadline.CancelAsync();
    await Parallel.ForEachAsync(connections.OfType<HubConnection>(), new ParallelOptions { MaxDegreeOfParallelism = 32 },
        async (connection, _) => await connection.DisposeAsync());
}

async Task UpdateAndDeliverAsync(CancellationToken stop)
{
    var expectedRevision = ++revision;
    var expectedKey = (expectedRevision - 1) % resultCount + 1;
    var expectedSequence = 1L + expectedRevision;
    await using (var update = (BlueTuskCommand)writer.CreateCommand())
    {
        update.CommandText = "UPDATE public.live_signalr_results SET revision = $1 WHERE id = $2";
        update.Parameters.Add(new BlueTuskParameter<int>(expectedRevision));
        update.Parameters.Add(new BlueTuskParameter<int>(expectedKey));
        if (await update.ExecuteNonQueryAsync(stop) != 1)
        {
            throw new InvalidDataException("The fixture did not commit one changed key.");
        }
    }

    if (!await deliveries.MoveNextAsync())
    {
        throw new EndOfStreamException("The committed change did not reach the PostgreSQL change feed.");
    }
    await using (var delivery = deliveries.Current)
    {
        if (delivery.Transaction.Changes.Count != 1)
        {
            throw new InvalidDataException("The fixture expected one change per transaction.");
        }
        if (invalidations is not null)
        {
            await invalidations.ConsumeTransactionAsync(delivery, stop);
        }
    }

    if (shared is not null)
    {
        if (await shared.RefreshAsync(stop) != 1)
        {
            throw new InvalidDataException("The candidate did not publish one Live event.");
        }
    }
    else
    {
        reference!.Publish(await ReadResultAsync(stop));
    }

    foreach (var client in clients)
    {
        var message = await client.ReadAsync(stop);
        var change = message.GetProperty("event");
        var row = change.GetProperty("row");
        if (message.GetProperty("kind").GetString() != "Event" || message.GetProperty("sequence").GetInt64() != expectedSequence ||
            change.GetProperty("kind").GetString() != "RowUpdated" || change.GetProperty("key").GetInt32() != expectedKey ||
            row.GetProperty("Id").GetInt32() != expectedKey || row.GetProperty("Revision").GetInt32() != expectedRevision)
        {
            throw new InvalidDataException("A SignalR client received a different sequence, key or revision.");
        }
        observedDeliveries++;
    }
}

async ValueTask<IReadOnlyList<ResultRow>> ReadResultAsync(CancellationToken stop)
{
    await using var command = (BlueTuskCommand)reader.CreateCommand();
    command.CommandText = "SELECT id, revision FROM public.live_signalr_results WHERE id >= $1 ORDER BY id LIMIT $2";
    command.Parameters.Add(new BlueTuskParameter<int>(1));
    command.Parameters.Add(new BlueTuskParameter<int>(resultCount));
    await using var result = await command.ExecuteReaderAsync(stop);
    var rows = new List<ResultRow>(resultCount);
    while (await result.ReadAsync(stop))
    {
        rows.Add(new ResultRow(result.GetInt32(0), result.GetInt32(1)));
    }
    if (rows.Count != resultCount)
    {
        throw new InvalidDataException("The shared PostgreSQL query returned the wrong number of rows.");
    }
    return rows;
}

internal static class SignalRCapture
{
    public const string AuthenticationScheme = "benchmark-loopback";
    public const string HubPath = "/benchmark/live";
    public const string QueryName = "signalr-benchmark";
}

internal sealed class FixtureAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Equals(Context.Connection.RemoteIpAddress, IPAddress.Loopback))
        {
            return Task.FromResult(AuthenticateResult.Fail("The benchmark only accepts local clients."));
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "benchmark")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }
}

internal sealed class FixtureResolver(ILiveSharedSubscription subscription) : ILiveTransportSubscriptionResolver
{
    public ValueTask<ILiveSharedSubscription> ResolveAsync(
        string query, JsonElement parameters, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (query != SignalRCapture.QueryName)
        {
            throw new LiveTransportRequestException("The fixture has one supported subscription.");
        }
        return ValueTask.FromResult(subscription);
    }
}
