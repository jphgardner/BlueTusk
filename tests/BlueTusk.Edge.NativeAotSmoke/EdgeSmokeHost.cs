using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.Edge.AspNetCore;
using BlueTusk.Edge.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Edge.SmokeHosting;

// Isolated test host only. A product host must supply its own trusted authentication and scope policy.
internal sealed class EdgeSmokeHost(WebApplication application, Uri endpoint) : IAsyncDisposable
{
    internal Uri Endpoint { get; } = endpoint;

    internal static async Task<EdgeSmokeHost> StartAsync(PostgreSqlEdgeServerStore store, bool browserCors = false,
        Func<DbConnection, DbTransaction, EdgeMutation, EdgeRecord, CancellationToken, ValueTask>? writeBusiness = null,
        Action<IHealthChecksBuilder>? additionalHealthChecks = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("edge-smoke").AddScheme<AuthenticationSchemeOptions, SmokeAuthentication>("edge-smoke", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("edge", policy => policy.AddAuthenticationSchemes("edge-smoke").RequireAuthenticatedUser()));
        var health = builder.Services.AddHealthChecks().AddBlueTuskEdgeServer("edge-readiness", store, new EdgeScope("tenant", "orders", 1));
        additionalHealthChecks?.Invoke(health);
        if (browserCors)
        {
            builder.Services.AddCors(options => options.AddPolicy("edge-smoke", policy => policy.SetIsOriginAllowed(static origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.Host == "127.0.0.1")
                .WithMethods("GET", "POST", "DELETE").WithHeaders("authorization", "content-type", "x-bluetusk-test-drop")));
        }
        var application = builder.Build();
        if (browserCors)
        {
            application.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"Edge HTTP smoke {context.Request.Method} {context.Request.Path} exception: {error}");
                    throw;
                }
                finally
                {
                    if (context.Request.Path.StartsWithSegments("/edge"))
                    {
                        Console.Error.WriteLine($"Edge HTTP smoke {context.Request.Method} {context.Request.Path} status={context.Response.StatusCode} aborted={context.RequestAborted.IsCancellationRequested} drop={context.Request.Headers.ContainsKey("x-bluetusk-test-drop")}");
                    }
                }
            });
        }
        if (browserCors) { application.UseCors("edge-smoke"); }
        application.UseAuthentication(); application.UseAuthorization();
        application.Use(async (context, next) =>
        {
            // Drop only a successful committed mutation response. A server error must
            // reach the browser so the smoke cannot mistake it for a lost commit.
            // Keep dropping marked replays: browsers may retry a broken connection.
            if (context.Request.Path == "/edge/mutations" && context.Request.Headers["x-bluetusk-test-drop"] == "until-reconnect")
            { context.Response.OnStarting(() => { if (context.Response.StatusCode == 200) context.Abort(); return Task.CompletedTask; }); }
            await next(context);
        });
        application.MapBlueTuskEdge(store, static (context, scope, _) => ValueTask.FromResult(
            context.User.FindFirstValue("tenant") == scope.Tenant && scope.Id == "orders" && scope.Epoch == 1), new EdgeEndpointOptions { AuthorizationPolicy = "edge", WriteBusinessAsync = writeBusiness });
        application.MapHealthChecks("/ops/edge").RequireAuthorization("edge");
        await application.StartAsync();
        return new EdgeSmokeHost(application, new Uri(application.Urls.Single().TrimEnd('/') + "/edge"));
    }
    public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }
    private sealed class SmokeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.Authorization == "Bearer edge-smoke-reader"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", "tenant")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }
}
