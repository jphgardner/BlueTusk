#if NET5_0
global using IResult = BlueTusk.Dashboard.LegacyDashboardHttpResult;
global using Results = BlueTusk.Dashboard.LegacyDashboardHttpResults;

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace BlueTusk.Dashboard;

internal sealed class LegacyDashboardHttpResult(Func<HttpContext, Task> execute)
{
    public Task ExecuteAsync(HttpContext context) => execute(context);
}

internal static class LegacyDashboardHttpResults
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IResult NotFound() => Status(StatusCodes.Status404NotFound);
    public static IResult Unauthorized() => Status(StatusCodes.Status401Unauthorized);
    public static IResult Forbid() => new(context => context.ForbidAsync());

    public static IResult Redirect(string location) => new(context =>
    {
        context.Response.Redirect(location);
        return Task.CompletedTask;
    });

    public static IResult Text(string value, string contentType) => new(context =>
    {
        context.Response.ContentType = contentType;
        return context.Response.WriteAsync(value, Encoding.UTF8, context.RequestAborted);
    });

    public static IResult Stream(Stream source, string contentType) => new(async context =>
    {
        await using var stream = source.ConfigureAwait(false);
        context.Response.ContentType = contentType;
        await source.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    });

    public static IResult BadRequest<T>(T value) => new(context =>
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, value,
            JsonOptions, context.RequestAborted);
    });

    public static IResult Problem(string? detail = null, string? title = null, int statusCode = 500,
        IDictionary<string, object?>? extensions = null) => new(context =>
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        var payload = new Dictionary<string, object?>
        {
            ["status"] = statusCode,
            ["title"] = title ?? ReasonPhrases.GetReasonPhrase(statusCode),
        };
        if (detail is not null)
        {
            payload.Add("detail", detail);
        }
        if (extensions is not null)
        {
            foreach (var item in extensions)
            {
                payload.TryAdd(item.Key, item.Value);
            }
        }
        return JsonSerializer.SerializeAsync(context.Response.Body, payload,
            cancellationToken: context.RequestAborted);
    });

    private static IResult Status(int code) => new(context =>
    {
        context.Response.StatusCode = code;
        return Task.CompletedTask;
    });
}
#endif
