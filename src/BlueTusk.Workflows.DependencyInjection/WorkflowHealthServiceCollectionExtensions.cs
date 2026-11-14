using BlueTusk.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Workflows.DependencyInjection;

public static class WorkflowHealthServiceCollectionExtensions
{
    /// <summary>Registers one trusted scope using an explicit typed store factory. Does not initialize schemas or start workers.</summary>
    public static IServiceCollection AddWorkflowsReadiness(this IServiceCollection services, string name,
        JobScope scope, Func<IServiceProvider, PostgreSqlWorkflowStore> storeFactory, WorkflowHealthCheckOptions options)
    {
        BlueTuskGuard.NotNull(services);
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotGreaterThan(name.Length, 64);
        BlueTuskGuard.NotNull(scope);
        BlueTuskGuard.NotNull(storeFactory);
        BlueTuskGuard.NotNull(options);
        options.Validate();
        int count = 0;
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is Registration registered)
            {
                count++;
                if (string.Equals(registered.Name, name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A Workflows readiness check with that name is already registered.");
                }
            }
        }
        BlueTuskGuard.NotGreaterThanOrEqual(count, 128);
        services.AddSingleton(new Registration(name));
        services.AddKeyedSingleton<WorkflowScopeHealthCheck>(name, (provider, _) => new WorkflowScopeHealthCheck(storeFactory(provider), scope, options));
        services.AddHealthChecks().Add(new HealthCheckRegistration(name, provider => provider.GetRequiredKeyedService<WorkflowScopeHealthCheck>(name),
            HealthStatus.Unhealthy, ["bluetusk", "workflows", "ready"], options.Timeout + TimeSpan.FromSeconds(1)));
        return services;
    }

    private sealed class Registration(string name)
    {
        internal string Name { get; } = name;
    }
}
