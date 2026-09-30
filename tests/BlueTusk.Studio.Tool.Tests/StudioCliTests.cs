using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueTusk.Studio.Tool.Tests;

public sealed class StudioCliTests
{
    [Fact]
    public async Task Actual_HTTP_uses_bearer_preserves_exact_metadata_and_rejects_redirects_and_payloads()
    {
        var previous = Environment.GetEnvironmentVariable("BLUETUSK_STUDIO_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("BLUETUSK_STUDIO_TOKEN", "test-only-secret-token");
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            var denied = false;
            var mode = 0;
            var path = string.Empty;
            app.Run(async context =>
            {
                Assert.Equal("Bearer test-only-secret-token", context.Request.Headers.Authorization.ToString());
                path = context.Request.Path + context.Request.QueryString;
                if (denied) { context.Response.StatusCode = 403; return; }
                if (mode == 1) { context.Response.Redirect("http://127.0.0.1:1/private-secret-token"); return; }
                context.Response.ContentType = "application/json";
                var body = mode switch
                {
                    2 => "{\"events\":[],\"payload\":\"private-secret-token\"}",
                    3 => "{\"events\":[],\"next\":null,\"extra\":\"" + new string('x', 1024 * 1024) + "\"}",
                    _ => "{\"events\":[{\"id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"sequence\":\"9223372036854775807\",\"type\":\"<script>unsafe</script>\",\"version\":1,\"occurredAt\":\"2026-09-27T00:00:00Z\",\"payloadBytes\":2}],\"next\":\"9223372036854775807\"}",
                };
                await context.Response.WriteAsync(body, context.RequestAborted);
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            var endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/bluetusk/studio/";
            using var output = new StringWriter();
            using var error = new StringWriter();
            var arguments = new[] { "events", "--endpoint", endpoint, "--stream", "orders", "--after", "42", "--limit", "2" };
            Assert.Equal(0, await StudioCli.RunAsync(arguments, output, error, TestContext.Current.CancellationToken));
            Assert.Contains("9223372036854775807", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", output.ToString(), StringComparison.Ordinal);
            Assert.Equal("/bluetusk/studio/events/?stream=orders&limit=2&after=42", path);
            denied = true;
            Assert.Equal(2, await StudioCli.RunAsync(arguments, output, error, TestContext.Current.CancellationToken));
            denied = false;
            for (mode = 1; mode <= 3; mode++)
            {
                output.GetStringBuilder().Clear();
                Assert.Equal(1, await StudioCli.RunAsync(arguments, output, error, TestContext.Current.CancellationToken));
                Assert.Empty(output.ToString());
                Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
            }
        }
        finally { Environment.SetEnvironmentVariable("BLUETUSK_STUDIO_TOKEN", previous); }
    }

    [Fact]
    public async Task Invalid_arguments_and_missing_authentication_never_reach_the_network()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await StudioCli.RunAsync(["--help"], output, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, await StudioCli.RunAsync(["events", "--endpoint", "http://example.com", "--stream", "orders"], output, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, await StudioCli.RunAsync(["streams", "--endpoint", "https://example.com", "--stream", "orders"], output, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, await StudioCli.RunAsync(["events", "--endpoint", "https://secret:password@example.com", "--stream", "orders"], output, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, await StudioCli.RunAsync(["events", "--endpoint", "https://example.com", "--stream", "orders", "--after", "0001"], output, error, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("password", error.ToString(), StringComparison.Ordinal);
    }
}
