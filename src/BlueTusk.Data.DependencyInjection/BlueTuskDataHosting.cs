using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Data.DependencyInjection;

/// <summary>Checks whether a BlueTusk data source can execute a PostgreSQL round trip.</summary>
public sealed class BlueTuskDataSourceHealthCheck : IHealthCheck
{
    private readonly BlueTuskDataSource _dataSource;

    public BlueTuskDataSourceHealthCheck(BlueTuskDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1
                ? HealthCheckResult.Healthy("BlueTusk completed a PostgreSQL round trip.")
                : HealthCheckResult.Unhealthy("BlueTusk returned an unexpected readiness result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "BlueTusk could not complete a PostgreSQL round trip.",
                exception);
        }
    }
}

/// <summary>Registers the BlueTusk ADO.NET provider with a .NET application host.</summary>
public static class BlueTuskDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers one singleton <see cref="BlueTuskDataSource"/> and a readiness health check.
    /// </summary>
    public static IServiceCollection AddDataSource(
        this IServiceCollection services,
        string connectionString,
        Action<BlueTuskDataSourceBuilder>? configure = null,
        string healthCheckName = "bluetusk")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(healthCheckName);

        services.TryAddSingleton(_ =>
        {
            var builder = new BlueTuskDataSourceBuilder(connectionString);
            configure?.Invoke(builder);
            return builder.Build();
        });
        services.TryAddSingleton<DbDataSource>(
            static provider => provider.GetRequiredService<BlueTuskDataSource>());
        services.TryAddSingleton<BlueTuskDataSourceHealthCheck>();
        services
            .AddHealthChecks()
            .AddCheck<BlueTuskDataSourceHealthCheck>(
                healthCheckName,
                failureStatus: HealthStatus.Unhealthy,
                tags: ["bluetusk", "ready"]);
        return services;
    }
}
