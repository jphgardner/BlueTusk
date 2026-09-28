using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.Studio;

public static class StudioEndpoints
{
    public static IServiceCollection AddBlueTuskStudio<TScopeResolver, TAuditSink>(this IServiceCollection services, StudioOptions options)
        where TScopeResolver : class, IStudioScopeResolver
        where TAuditSink : class, IStudioAuditSink
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddAntiforgery();
        services.AddSingleton(options);
        services.AddSingleton<StudioQueryService>();
        services.TryAddScoped<TScopeResolver>();
        services.TryAddScoped<TAuditSink>();
        services.AddScoped<IStudioScopeResolver>(provider => provider.GetRequiredService<TScopeResolver>());
        services.AddScoped<IStudioAuditSink>(provider => provider.GetRequiredService<TAuditSink>());
        return services;
    }

    public static RouteGroupBuilder MapBlueTuskStudio(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<StudioOptions>();
        var group = endpoints.MapGroup(options.Path).RequireAuthorization(options.ReadPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            context.HttpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            return await next(context).ConfigureAwait(false);
        });
        group.MapGet("/", (HttpContext context) => context.Request.Path.Value?.EndsWith('/') == true
            ? Results.Content(Asset("studio.html"), "text/html", Encoding.UTF8)
            : Results.Redirect(options.Path + "/"));
        group.MapGet("/assets/studio.js", () => Results.Content(Asset("studio.js"), "text/javascript", Encoding.UTF8));
        group.MapGet("/assets/studio.css", () => Results.Content(Asset("studio.css"), "text/css", Encoding.UTF8));
        group.MapGet("/session", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new StudioSession(tokens.HeaderName ?? throw new InvalidOperationException("Studio needs an antiforgery request header."),
                tokens.RequestToken ?? throw new InvalidOperationException("Studio could not create an antiforgery token.")), StudioJsonContext.Default.StudioSession);
        });
        group.MapGet("/schema", async (HttpContext context, IStudioScopeResolver resolver, StudioQueryService service) =>
        {
            try
            {
                var scope = await resolver.ResolveAsync(context.User, context.RequestAborted).ConfigureAwait(false);
                return Results.Bytes((await service.CaptureSchemaAsync(scope, context.RequestAborted).ConfigureAwait(false)).ToArray(), "application/json");
            }
            catch (StudioCapacityException) { return Results.StatusCode(StatusCodes.Status429TooManyRequests); }
            catch (StudioReplyLimitException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (OperationCanceledException) { return Results.StatusCode(StatusCodes.Status408RequestTimeout); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
        });
        group.MapPost("/query", ExecuteQueryAsync).RequireAuthorization(options.QueryPolicy);
        return group;
    }

    private static async Task ExecuteQueryAsync(HttpContext context, StudioOptions options, IAntiforgery antiforgery,
        IStudioScopeResolver resolver, IStudioAuditSink audit, StudioQueryService service)
    {
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        StudioQueryRequest request;
        try
        {
            var body = await ReadBoundedAsync(context.Request, options.MaximumRequestBytes, context.RequestAborted).ConfigureAwait(false);
            request = JsonSerializer.Deserialize(body.Span, StudioJsonContext.Default.StudioQueryRequest)
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var operation = Guid.NewGuid();
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "authenticated";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Sql ?? string.Empty)));
        await audit.RecordAsync(new(operation, actor, fingerprint, "attempt", 0), context.RequestAborted).ConfigureAwait(false);
        try
        {
            var scope = await resolver.ResolveAsync(context.User, context.RequestAborted).ConfigureAwait(false);
            var result = await service.ExecuteAsync(scope, request, context.RequestAborted).ConfigureAwait(false);
            await audit.RecordAsync(new(operation, actor, fingerprint, "completed", result.Rows), context.RequestAborted).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(result.Json, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await audit.RecordAsync(new(operation, actor, fingerprint, "failed", 0), CancellationToken.None).ConfigureAwait(false);
            context.Response.StatusCode = exception switch
            {
                StudioCapacityException => StatusCodes.Status429TooManyRequests,
                ArgumentException => StatusCodes.Status400BadRequest,
                StudioReplyLimitException => StatusCodes.Status413PayloadTooLarge,
                OperationCanceledException => StatusCodes.Status408RequestTimeout,
                _ => StatusCodes.Status422UnprocessableEntity,
            };
            // Database errors may contain SQL literals or data; they are never copied
            // into browser error bodies or logs by Studio.
        }
    }

    private static async ValueTask<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpRequest request, int maximum, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximum) { throw new ArgumentException("Studio request exceeds its body bound."); }
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > maximum) { throw new ArgumentException("Studio request exceeds its body bound."); }
                body.Write(buffer, 0, read);
            }
            return body.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static string Asset(string name)
    {
        using var stream = typeof(StudioEndpoints).Assembly.GetManifestResourceStream("BlueTusk.Studio.Assets." + name)
            ?? throw new InvalidOperationException("A packaged Studio asset is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

internal sealed record StudioSession(string Header, string Token);

[JsonSourceGenerationOptions(MaxDepth = 8, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StudioSession))]
[JsonSerializable(typeof(StudioQueryRequest))]
internal sealed partial class StudioJsonContext : JsonSerializerContext;
