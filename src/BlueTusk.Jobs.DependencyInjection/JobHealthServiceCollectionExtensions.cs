using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Jobs.DependencyInjection;

public static class JobHealthServiceCollectionExtensions
{
    /// <summary>Registers one trusted scope using an explicit typed store factory. Does not initialize schemas or start workers.</summary>
    public static IServiceCollection AddJobsReadiness(this IServiceCollection services, string name,
        JobScope scope, Func<IServiceProvider, PostgreSqlJobStore> storeFactory, JobHealthCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, 64);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(storeFactory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        int count = 0;
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is Registration registered)
            {
                count++;
                if (string.Equals(registered.Name, name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A Jobs readiness check with that name is already registered.");
                }
            }
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(count, 128);
        services.AddSingleton(new Registration(name));
        services.AddKeyedSingleton<JobQueueHealthCheck>(name, (provider, _) => new JobQueueHealthCheck(storeFactory(provider), scope, options));
        services.AddHealthChecks().Add(new HealthCheckRegistration(name, provider => provider.GetRequiredKeyedService<JobQueueHealthCheck>(name),
            HealthStatus.Unhealthy, ["bluetusk", "jobs", "ready"], options.Timeout + TimeSpan.FromSeconds(1)));
        return services;
    }

    private sealed class Registration(string name)
    {
        internal string Name { get; } = name;
    }
}
