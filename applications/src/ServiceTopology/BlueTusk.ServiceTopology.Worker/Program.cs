using BlueTusk.Applications.Hosting;
using BlueTusk.ContinuousGraph;
using BlueTusk.ServiceTopology.Infrastructure;
using BlueTusk.Streams.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.AddWorkerObservability("service-topology-worker");
var connectionString = builder.Configuration.GetConnectionString("Primary") ??
    throw new InvalidOperationException("Connection string 'Primary' is required.");
builder.Services.AddTopologyInfrastructure(connectionString);
builder.Services.AddBlueTuskStreams();
builder.Services.AddHostedService<TopologyRecoveryWorker>();
await builder.Build().RunAsync();

internal sealed class TopologyRecoveryWorker(
    IServiceScopeFactory scopes,
    ILogger<TopologyRecoveryWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Version?, Exception?> RuntimeStarted =
        LoggerMessage.Define<Version?>(
            LogLevel.Information,
            new EventId(1, "RuntimeStarted"),
            "ContinuousGraph runtime {Version} is active for topology repair and checkpoint recovery.");
    private static readonly Action<ILogger, Exception?> StoreDisconnected =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, "StoreDisconnected"),
            "Authoritative topology store is disconnected; retrying from checkpoint.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RuntimeStarted(logger, typeof(ContinuousGraphQueryCompiler).Assembly.GetName().Version, null);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<TopologyDbContext>();
            if (!await database.Database.CanConnectAsync(stoppingToken).ConfigureAwait(false))
            {
                StoreDisconnected(logger, null);
            }
        }
    }
}
