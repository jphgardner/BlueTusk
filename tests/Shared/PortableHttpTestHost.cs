using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if !NET7_0_OR_GREATER
using DbDataSource = BlueTusk.Data.Compatibility.BlueTuskLegacyDataSource;
#endif
#if NET5_0
using DbBatch = BlueTusk.Data.Compatibility.BlueTuskLegacyBatch;
#endif

namespace BlueTusk.Tests.Shared;

internal sealed class PortableHttpTestHost(IHost host, Uri address) : IAsyncDisposable
{
    public Uri Address { get; } = address;

    public IServiceProvider Services => host.Services;

    public static async Task<PortableHttpTestHost> StartAsync(
        Action<IServiceCollection> configureServices,
        Action<IEndpointRouteBuilder> configureEndpoints,
        CancellationToken cancellationToken = default,
        int port = 0,
        Action<IApplicationBuilder>? configureMiddleware = null)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(webHost => webHost
                .ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port))
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication();
                    services.AddAuthorization();
                    configureServices(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    configureMiddleware?.Invoke(app);
                    app.UseEndpoints(configureEndpoints);
                }))
            .Build();
        try
        {
            await host.StartAsync(cancellationToken);
            var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>() ??
                throw new InvalidOperationException("Kestrel did not report its test address.");
            return new PortableHttpTestHost(host, new Uri(addresses.Addresses.Single()));
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (host)
        {
            await host.StopAsync();
        }
    }
}

internal abstract class PortableAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    protected PortableAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
#if NET8_0_OR_GREATER
        : base(options, logger, encoder)
#else
        : base(options, logger, encoder, new SystemClock())
#endif
    {
    }
}

internal abstract class PortableFixtureDataSource : DbDataSource
{
#if !NET7_0_OR_GREATER
    protected override DbConnection OpenDbConnection() => throw new NotSupportedException("Use the asynchronous test fixture.");

    protected override DbCommand CreateDbCommand([AllowNull] string commandText) => throw new NotSupportedException();

    protected override DbBatch CreateDbBatch() => throw new NotSupportedException();
#endif
}
