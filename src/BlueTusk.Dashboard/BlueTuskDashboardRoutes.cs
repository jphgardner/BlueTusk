using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BlueTusk.Dashboard;

internal sealed class BlueTuskDashboardRoutes(
    IEndpointRouteBuilder endpoints,
    string prefix,
    string readPolicy)
{
    public void MapGet(string path, Func<IResult> handler) =>
        MapGet(path, context => Task.FromResult(handler()));

    public void MapGet(string path, Func<HttpContext, Task<IResult>> handler) =>
        Map(path, HttpMethods.Get, handler);

    public IEndpointConventionBuilder MapPost(string path, Func<HttpContext, Task<IResult>> handler) =>
        Map(path, HttpMethods.Post, handler);

    private IEndpointConventionBuilder Map(string path, string method, Func<HttpContext, Task<IResult>> handler)
    {
        var pattern = prefix == "/" ? path : prefix + path;
        return endpoints.MapMethods(pattern, [method], async context =>
        {
            var result = await handler(context).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        }).RequireAuthorization(readPolicy);
    }
}
