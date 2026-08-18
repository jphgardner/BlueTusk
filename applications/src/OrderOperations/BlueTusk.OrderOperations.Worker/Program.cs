using System.Data.Common;
using BlueTusk.Applications.Hosting;
using BlueTusk.OrderOperations.Infrastructure;
using BlueTusk.Streams.DependencyInjection;
using BlueTusk.Sync.DependencyInjection;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.AddWorkerObservability("order-operations-worker");
var connectionString = builder.Configuration.GetConnectionString("Primary") ??
    throw new InvalidOperationException("Connection string 'Primary' is required.");
builder.Services.AddOrderInfrastructure(connectionString);
builder.Services.AddBlueTuskStreams();
builder.Services.AddBlueTuskSync();
builder.Services.AddHostedService<AuditRelayWorker>();
await builder.Build().RunAsync();

internal sealed class AuditRelayWorker(
    IServiceScopeFactory scopes,
    ILogger<AuditRelayWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> Relayed =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, "AuditRelayed"),
            "Relayed {Count} immutable order audit records.");
    private static readonly Action<ILogger, Exception?> StoreUnavailable =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, "StoreUnavailable"),
            "Order audit store is unavailable; relay remains alive and will retry.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
                var pending = await database.Audit
                    .Where(entry => entry.RelayedAt == null)
                    .OrderBy(entry => entry.Id)
                    .Take(200)
                    .ToArrayAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (pending.Length == 0)
                {
                    continue;
                }

                var relayedAt = DateTimeOffset.UtcNow;
                foreach (var entry in pending)
                {
                    entry.RelayedAt = relayedAt;
                }

                await database.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                Relayed(logger, pending.Length, null);
            }
            catch (Exception exception) when (exception is DbException or DbUpdateException)
            {
                StoreUnavailable(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
