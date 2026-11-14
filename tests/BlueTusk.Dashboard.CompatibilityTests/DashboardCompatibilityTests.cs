using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Dashboard.CompatibilityTests;

public sealed class DashboardCompatibilityTests
{
    private static readonly string[] MutationPolicies = ["dashboard-read", "dashboard-mutate"];

    [Fact]
    public async Task Host_routes_dashboard_requests_and_enforces_read_and_mutation_policies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IEndpointRouteBuilder? routes = null;
        using var host = new HostBuilder().ConfigureWebHost(web => web
            .UseKestrel()
            .UseUrls("http://127.0.0.1:0")
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("dashboard-read", policy => policy.RequireRole("reader"));
                    options.AddPolicy("dashboard-mutate", policy => policy.RequireRole("operator"));
                    options.AddPolicy("dashboard-graph", policy => policy.RequireRole("graph"));
                });
            })
            .Configure(application => application.UseRouting().UseAuthentication().UseAuthorization().UseEndpoints(endpoints =>
            {
                routes = endpoints;
                endpoints.MapBlueTuskDashboard(options =>
                {
                    options.RoutePrefix = "/operations";
                    options.ReadAuthorizationPolicy = "dashboard-read";
                    options.MutationAuthorizationPolicy = "dashboard-mutate";
                    options.GraphExecutionAuthorizationPolicy = "dashboard-graph";
                });
            })))
            .Build();
        await host.StartAsync(deadline.Token);

        var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        using var anonymous = await client.GetAsync("/operations/api/capabilities", deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using var redirect = await client.GetAsync("/operations", deadline.Token);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/operations/overview", redirect.Headers.Location?.OriginalString);
        using var capabilities = await client.GetAsync("/operations/api/capabilities", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        Assert.Equal("application/json", capabilities.Content.Headers.ContentType?.MediaType);
        using var response = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, response.RootElement.ValueKind);
        Assert.NotEmpty(response.RootElement.EnumerateObject());
        using var mutation = new HttpRequestMessage(HttpMethod.Post, "/operations/api/v1/operations");
        using var denied = await client.SendAsync(mutation, deadline.Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var endpoint = Assert.Single(routes!.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/operations/api/v1/operations");
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(metadata => metadata.Policy);
        Assert.Equal(MutationPolicies, policies);
    }

    private sealed class TestAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
#if NET8_0_OR_GREATER
            : base(options, logger, encoder)
#else
            : base(options, logger, encoder, new SystemClock())
#endif
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
        }
    }
}
