using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BlueTusk.Applications.Hosting;

public static class ProductionHosting
{
    public static WebApplicationBuilder AddProductionHosting(
        this WebApplicationBuilder builder,
        string applicationName,
        string rolePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rolePrefix);

        builder.Services.AddProblemDetails();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddHealthChecks();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("writes", limiter =>
            {
                limiter.PermitLimit = 60;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
        });

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = $"__Host-{applicationName.ToLowerInvariant()}";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            })
            .AddOpenIdConnect(options =>
            {
                var section = builder.Configuration.GetSection("Authentication");
                options.Authority = section["Authority"];
                options.ClientId = section["ClientId"];
                options.ClientSecret = section["ClientSecret"];
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            });

        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = options.DefaultPolicy;
            options.AddPolicy("Viewer", policy => policy.RequireRole(
                $"{rolePrefix}.viewer",
                $"{rolePrefix}.operator",
                $"{rolePrefix}.admin"));
            options.AddPolicy("Operator", policy => policy.RequireRole(
                $"{rolePrefix}.operator",
                $"{rolePrefix}.admin"));
            options.AddPolicy("Administrator", policy =>
                policy.RequireRole($"{rolePrefix}.admin"));
            options.AddPolicy("BlueTusk.ControlPlane.Read", policy => policy.RequireRole(
                $"{rolePrefix}.operator",
                $"{rolePrefix}.admin"));
            options.AddPolicy("BlueTusk.ControlPlane.Mutate", policy =>
                policy.RequireRole($"{rolePrefix}.admin"));
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(applicationName))
            .WithTracing(tracing => tracing
                .AddSource("BlueTusk.*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(
                    "BlueTusk.Streams",
                    "BlueTusk.Sync",
                    "BlueTusk.Live",
                    "BlueTusk.ContinuousGraph")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
        });
        return builder;
    }

    public static IHostApplicationBuilder AddWorkerObservability(
        this IHostApplicationBuilder builder,
        string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(applicationName))
            .WithTracing(tracing => tracing
                .AddSource("BlueTusk.*")
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(
                    "BlueTusk.Streams",
                    "BlueTusk.Sync",
                    "BlueTusk.Live",
                    "BlueTusk.ContinuousGraph")
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());
        return builder;
    }

    public static WebApplication UseProductionHosting(this WebApplication app)
    {
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }
        app.UseExceptionHandler();
        app.UseHttpsRedirection();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; connect-src 'self'; img-src 'self' data:; " +
                "style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Permissions-Policy"] =
                "camera=(), microphone=(), geolocation=()";
            await next().ConfigureAwait(false);
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous();
        return app;
    }

    public static IEndpointRouteBuilder MapBffSessionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/session", (HttpContext context) => Results.Ok(new
        {
            authenticated = context.User.Identity?.IsAuthenticated is true,
            name = context.User.Identity?.Name,
            tenant = context.User.FindFirstValue("tenant_id"),
            roles = context.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value),
        })).RequireAuthorization();

        endpoints.MapGet("/api/v1/session/csrf", (
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        }).RequireAuthorization();

        endpoints.MapPost("/api/v1/session/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                .ConfigureAwait(false);
            await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme)
                .ConfigureAwait(false);
            return Results.NoContent();
        }).RequireBffMutation();
        return endpoints;
    }

    public static RouteHandlerBuilder RequireBffMutation(this RouteHandlerBuilder builder) =>
        builder
            .RequireAuthorization("Operator")
            .RequireRateLimiting("writes")
            .AddEndpointFilter(async (context, next) =>
            {
                var antiforgery = context.HttpContext.RequestServices
                    .GetRequiredService<IAntiforgery>();
                await antiforgery.ValidateRequestAsync(context.HttpContext)
                    .ConfigureAwait(false);
                return await next(context).ConfigureAwait(false);
            });

    public static string RequiredConnectionString(
        this IConfiguration configuration,
        string name = "Primary")
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Connection string '{name}' is required."));
        }
        return value;
    }
}
