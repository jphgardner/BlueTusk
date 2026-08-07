using BlueTusk.Applications.Hosting;
using BlueTusk.ContinuousGraph;
using BlueTusk.FraudInvestigation.Infrastructure;
using BlueTusk.Streams.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.AddWorkerObservability("fraud-investigation-worker");
var connectionString = builder.Configuration.GetConnectionString("Primary") ??
    throw new InvalidOperationException("Connection string 'Primary' is required.");
builder.Services.AddFraudInfrastructure(connectionString);
builder.Services.AddBlueTuskStreams();
builder.Services.AddHostedService<FraudGraphRecoveryWorker>();
await builder.Build().RunAsync();

internal sealed class FraudGraphRecoveryWorker(
    IServiceScopeFactory scopes,
    ILogger<FraudGraphRecoveryWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Version?, Exception?> RuntimeStarted =
        LoggerMessage.Define<Version?>(
            LogLevel.Information,
            new EventId(1, "RuntimeStarted"),
            "ContinuousGraph runtime {Version} is active for suspicious-path evaluation.");
    private static readonly Action<ILogger, Exception?> StoreDisconnected =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, "StoreDisconnected"),
            "Fraud graph store is disconnected; evaluation will resume from replay state.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RuntimeStarted(logger, typeof(ContinuousGraphQueryCompiler).Assembly.GetName().Version, null);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
            if (!await database.Database.CanConnectAsync(stoppingToken).ConfigureAwait(false))
            {
                StoreDisconnected(logger, null);
            }
        }
    }
}
