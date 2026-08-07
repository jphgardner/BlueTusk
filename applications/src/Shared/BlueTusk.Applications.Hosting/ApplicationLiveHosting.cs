using System.Security.Claims;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Live.ServerSentEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.Applications.Hosting;

public sealed record ApplicationLiveOptions(
    string Capability,
    string DatabaseIdentity,
    string Schema,
    string Table,
    IReadOnlyList<string> Columns);

public static class ApplicationLiveHosting
{
    public static IServiceCollection AddApplicationLive(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString,
        ApplicationLiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var keyText = configuration["BlueTusk:Live:ResumeSigningKey"];
        if (string.IsNullOrWhiteSpace(keyText))
        {
            throw new InvalidOperationException(
                "BlueTusk:Live:ResumeSigningKey must contain a base64-encoded 32-byte secret.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "BlueTusk:Live:ResumeSigningKey is not valid base64.",
                exception);
        }

        var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        var tenantDataSources = configuration
            .GetSection("BlueTusk:Live:TenantConnectionStrings")
            .GetChildren()
            .Where(section => !string.IsNullOrWhiteSpace(section.Value))
            .ToDictionary(
                section => section.Key,
                section => new BlueTuskDataSourceBuilder(section.Value!).Build(),
                StringComparer.Ordinal);
        if (tenantDataSources.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one BlueTusk:Live:TenantConnectionStrings entry is required. " +
                "Each entry must use a tenant-specific read-only role protected by PostgreSQL RLS.");
        }
        var store = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = "bluetusk_live",
        });
        var policy = new LiveClientQueryPolicy(
            options.Capability,
            "v1",
            options.DatabaseIdentity,
            LiveClientSecurityMode.DatabaseRowLevelSecurity |
                LiveClientSecurityMode.DedicatedReadOnlyRole,
            [new LiveClientRelation(options.Schema, options.Table, options.Columns)],
            maximumResultCount: 1_000);
        services.AddSingleton(dataSource);
        foreach (var tenantDataSource in tenantDataSources.Values)
        {
            services.AddSingleton(tenantDataSource);
        }
        services.AddSingleton(store);
        services.AddSingleton<ILiveInvalidationLog>(store);
        services.AddSingleton<ILiveReplayStore>(store);
        services.AddSingleton(new LiveSharedSubscriptionRegistry());
        services.AddSingleton<ILiveClientQueryAuthorizer>(
            new TenantLiveQueryAuthorizer(tenantDataSources, policy, options.Capability));
        services.AddSingleton<ILiveTransportSubscriptionResolver, LiveClientQueryTransportResolver>();
        services.AddBlueTuskLiveAspNetCore(new LiveResumeTokenProtector(
            [new LiveResumeTokenKey("primary", key, isPrimary: true)]));
        return services;
    }

    public static RouteHandlerBuilder MapApplicationLive(
        this IEndpointRouteBuilder endpoints) =>
        endpoints.MapBlueTuskLiveServerSentEvents("/api/v1/live")
            .RequireAuthorization("Viewer")
            .RequireRateLimiting("writes");

    private sealed class TenantLiveQueryAuthorizer(
        IReadOnlyDictionary<string, BlueTuskDataSource> dataSources,
        LiveClientQueryPolicy policy,
        string capability) : ILiveClientQueryAuthorizer
    {
        public ValueTask<LiveClientQueryGrant?> AuthorizeAsync(
            string requestedCapability,
            LiveClientQueryDefinition definition,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(definition);
            var tenant = principal.FindFirstValue("tenant_id");
            if (principal.Identity?.IsAuthenticated is not true ||
                string.IsNullOrWhiteSpace(tenant) ||
                !string.Equals(requestedCapability, capability, StringComparison.Ordinal) ||
                !dataSources.TryGetValue(tenant, out var dataSource))
            {
                return ValueTask.FromResult<LiveClientQueryGrant?>(null);
            }

            return ValueTask.FromResult<LiveClientQueryGrant?>(new LiveClientQueryGrant(
                dataSource,
                policy,
                new LiveSecurityScope($"tenant:{tenant}", $"{capability}:v1")));
        }
    }
}
