#if !NET7_0_OR_GREATER
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BlueTusk.Studio;

internal sealed class LegacyStudioRoutes(
    IEndpointRouteBuilder endpoints,
    string prefix,
    string policy,
    bool contentSecurityPolicy = false) : IEndpointConventionBuilder
{
    private readonly List<IEndpointConventionBuilder> _routes = [];

    public void MapGet(string pattern, Func<LegacyStudioResult> request) =>
        MapGet(pattern, context => request()(context));

    public void MapGet(string pattern, Func<HttpContext, LegacyStudioResult> request) =>
        MapGet(pattern, context => request(context)(context));

    public void MapGet(string pattern, Func<HttpContext, Task<LegacyStudioResult>> request) =>
        MapGet(pattern, async context =>
        {
            var result = await request(context).ConfigureAwait(false);
            await result(context).ConfigureAwait(false);
        });

    public IEndpointConventionBuilder MapGet(string pattern, RequestDelegate request) => Map(pattern, HttpMethods.Get, request);

    public IEndpointConventionBuilder MapPost(string pattern, RequestDelegate request) => Map(pattern, HttpMethods.Post, request);

    public void Add(Action<EndpointBuilder> convention)
    {
        foreach (var route in _routes)
        {
            route.Add(convention);
        }
    }

    private IEndpointConventionBuilder Map(string pattern, string method, RequestDelegate request)
    {
        var route = endpoints.MapMethods(prefix.TrimEnd('/') + pattern, [method], async context =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (contentSecurityPolicy)
            {
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            }

            await request(context).ConfigureAwait(false);
        }).RequireAuthorization(policy);
        _routes.Add(route);
        return route;
    }
}

internal delegate Task LegacyStudioResult(HttpContext context);

internal static class LegacyStudioResults
{
    public static LegacyStudioResult Content(string text, string contentType, Encoding encoding) => context =>
    {
        context.Response.ContentType = contentType + "; charset=" + encoding.WebName;
        return context.Response.WriteAsync(text, encoding, context.RequestAborted);
    };

    public static LegacyStudioResult Redirect(string address) => context =>
    {
        context.Response.Redirect(address);
        return Task.CompletedTask;
    };

    public static LegacyStudioResult Json<T>(T value, JsonTypeInfo<T> typeInfo) => context =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, context.RequestAborted);
    };

    public static LegacyStudioResult StatusCode(int status) => context =>
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    };

    public static LegacyStudioResult Bytes(byte[] bytes, string contentType) => context =>
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        return context.Response.Body.WriteAsync(bytes.AsMemory(), context.RequestAborted).AsTask();
    };
}
#endif
