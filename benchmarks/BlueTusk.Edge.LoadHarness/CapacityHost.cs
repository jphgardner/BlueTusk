using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.Edge;
using BlueTusk.Edge.AspNetCore;
using BlueTusk.Edge.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Edge.LoadHarness;

// Disposable loopback fixture. A deployment must supply its own identity and scope policy.
internal sealed class CapacityHost(WebApplication application, Uri endpoint) : IAsyncDisposable
{
    internal Uri Endpoint { get; } = endpoint;

    internal static async Task<CapacityHost> StartAsync(PostgreSqlEdgeServerStore store, string schema, int port,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddAuthentication("edge-capacity")
            .AddScheme<AuthenticationSchemeOptions, CapacityAuthentication>("edge-capacity", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("edge-capacity", policy =>
            policy.AddAuthenticationSchemes("edge-capacity").RequireAuthenticatedUser()));
        builder.Services.AddCors(options => options.AddPolicy("edge-capacity-browser", policy => policy
            .SetIsOriginAllowed(static origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                uri.Scheme == "http" && uri.Host == "127.0.0.1")
            .WithMethods("GET", "POST", "DELETE").WithHeaders("authorization", "content-type")));
        var application = builder.Build();
        application.UseCors("edge-capacity-browser");
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapBlueTuskEdge(store, static (context, scope, _) => ValueTask.FromResult(
            context.User.FindFirstValue("tenant") == scope.Tenant && scope.Id == "orders" && scope.Epoch == 1),
            new EdgeEndpointOptions
            {
                AuthorizationPolicy = "edge-capacity",
                WriteBusinessAsync = async (connection, transaction, mutation, record, token) =>
                {
                    if (record.Id != mutation.DocumentId) { throw new InvalidOperationException("Business record identity changed."); }
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"INSERT INTO \"{schema}\".business_effects(mutation_id,tenant,document_id) VALUES(@id,@tenant,@document)";
                    Add(command, "id", mutation.Id); Add(command, "tenant", mutation.Scope.Tenant);
                    Add(command, "document", mutation.DocumentId);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    { throw new InvalidOperationException("The business write was not committed exactly once."); }
                }
            });
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        return new CapacityHost(application, new Uri($"http://127.0.0.1:{port}/edge"));
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync().ConfigureAwait(false);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class CapacityAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            const string prefix = "Bearer edge-capacity-";
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith(prefix, StringComparison.Ordinal)) { return Task.FromResult(AuthenticateResult.NoResult()); }
            var tenant = header[prefix.Length..];
            if (tenant.Length != 9 || !tenant.StartsWith("tenant-", StringComparison.Ordinal) ||
                !int.TryParse(tenant.AsSpan(7), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index) || index is < 0 or > 7)
            { return Task.FromResult(AuthenticateResult.NoResult()); }
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", tenant)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
