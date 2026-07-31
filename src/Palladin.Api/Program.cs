using System.Net;
using System.Text;
using System.Text.Json;
using Palladin.Core.Json;
using FastEndpoints;
using FastEndpoints.Swagger;
using Palladin.Api.Framework;
using Palladin.Api.Middlewares;
using Palladin.Core.Analytics;
using Palladin.Core.Ai;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Hangfire;
using Palladin.Core.MassTransit;
using Palladin.Core.Options;
using Palladin.Core.Security;
using Palladin.Core.Transport;
using Microsoft.AspNetCore.HttpOverrides;
using Palladin.Module.Agents;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Audit;
using Palladin.Module.Identity;
using Palladin.Module.Identity.Shared;
using Palladin.Module.Notification;
using Palladin.Module.Search;
using Palladin.Module.PublicAssetCatalog;
using Palladin.Module.Vault;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NodaTime;
using NodaTime.Text;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("Configuration/pushtext.json", optional: false, reloadOnChange: true);

var configuration = builder.Configuration;

ConfigureLogging();

builder.Services
    .AddFastEndpoints()
    .SwaggerDocument(o =>
    {
        o.AutoTagPathSegmentIndex = 0;
        o.SerializerSettings = options =>
        {
            options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        };
    });

var networkingOptions =
    configuration.GetSection(NetworkingOptions.Position).Get<NetworkingOptions>() ?? new NetworkingOptions();
var trustForwardedHeaders = networkingOptions.TrustForwardedHeaders;
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust forwarded headers only from explicitly configured proxies, else a client could spoof its source IP.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in networkingOptions.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out var proxyAddress))
            {
                options.KnownProxies.Add(proxyAddress);
            }
        }

        foreach (var network in networkingOptions.KnownNetworks)
        {
            var parts = network.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var prefix)
                && int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
            }
        }
    });
}

builder.Services
    .AddGeneralOptions(configuration)
    .AddHangfireModule(configuration.GetSection("Hangfire"))
    .AddSingleton(TimeProvider.System)
    .AddSingleton<IClock>(SystemClock.Instance)
    .AddGuidModule()
    .AddMemoryCache()
    .AddHttpClient()
    .AddTransportModule();

builder.Services
    .AddIdentityModule(configuration)
    .AddVaultModule(configuration)
    .AddAgentsModule(configuration)
    .AddNotificationModule(configuration)
    .AddAuditModule(configuration)
    .AddSearchModule(configuration)
    .AddPublicAssetCatalogModule(configuration);

builder.Services
    .AddMassTransitModule(configuration);

var jwtSection = configuration.GetSection("Modules:Identity:Jwt");

// An empty or short JWT signing secret lets an attacker forge access tokens — refuse to start with one.
if (!builder.Environment.IsDevelopment())
{
    var jwtSecret = jwtSection["Secret"];
    if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    {
        throw new InvalidOperationException(
            "Modules:Identity:Jwt:Secret must be configured with at least 32 bytes in non-Development environments.");
    }
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Secret"]!)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?.GetUserId();
                var organizationId = context.Principal?.GetOrganizationId();
                if (userId is null || organizationId is null)
                {
                    context.Fail("organization-membership-invalid");
                    return;
                }

                var validator = context.HttpContext.RequestServices
                    .GetRequiredService<IOrganizationMembershipValidator>();
                if (!await validator.IsActiveAsync(userId.Value, organizationId.Value, context.HttpContext.RequestAborted))
                {
                    context.Fail("organization-membership-invalid");
                }
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments(NotificationModule.HubPath))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    })
    .AddAgentAuthentication();

builder.Services.AddAuthorization();

builder.Services.AddPalladinRateLimiter();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "https://palladin.io")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.AddAnalyticsModule();

var app = builder.Build();

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

// HSTS + redirect keep credentials off plaintext transport; skipped only in Development and Testing (no TLS).
var enforceHttps = !app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing");
if (enforceHttps)
{
    // Behind a proxy without trusted-proxy config, rate-limiting collapses to one bucket and HTTPS redirect loops.
    var hasTrustedProxyConfig = networkingOptions.TrustForwardedHeaders
        && (networkingOptions.KnownProxies.Length > 0 || networkingOptions.KnownNetworks.Length > 0);
    if (!hasTrustedProxyConfig)
    {
        app.Logger.LogCritical(
            "HTTPS enforcement is active in {Environment} but Networking:TrustForwardedHeaders is off or no "
            + "KnownProxies/KnownNetworks are configured. Behind a reverse proxy this breaks rate-limiting "
            + "(all clients collapse into a single proxy-IP partition → global throttle) and HTTPS redirection "
            + "(the proxy's http hop causes redirect loops). Staging/Production MUST set "
            + "Networking:TrustForwardedHeaders=true and populate KnownProxies (proxy IPs) or KnownNetworks (CIDR).",
            app.Environment.EnvironmentName);
    }

    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseMiddleware<FillTransportHeadersMiddleware>();

app.UseCors();
app.UseAuthentication();

// Skip enforcement under the in-memory Testing host: the suite drives many auth/agent requests from a
// single loopback IP within one window and must not be throttled. The limiter is active in every real env.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseRateLimiter();
}

app.UseAuthorization();

app.UseFastEndpoints(config =>
{
    config.Serializer.Options.AddPalladinDefaultConfiguration();
    config.Serializer.Options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    // Query/route binding for NodaTime Instant — the JSON serializer only covers bodies,
    // so without this every `?from=2026-07-02T22:00:00Z` query param 400s.
    config.Binding.ValueParserFor<Instant>(input =>
    {
        var parsed = InstantPattern.ExtendedIso.Parse(input.ToString() ?? string.Empty);
        return new(parsed.Success, parsed.Success ? parsed.Value : default);
    });
    config.Binding.ValueParserFor<Instant?>(input =>
    {
        var parsed = InstantPattern.ExtendedIso.Parse(input.ToString() ?? string.Empty);
        return new(parsed.Success, parsed.Success ? (Instant?)parsed.Value : null);
    });
    config.Endpoints.Configurator = endpointDefinition =>
    {
        endpointDefinition.PostProcessor<ExceptionGlobalPostProcessor>(Order.Before);
        if (app.Configuration.GetValue("KillSwitch:RestApi", false))
        {
            endpointDefinition.PreProcessor<KillSwitchGlobalPreProcessor>(Order.Before);
        }
    };
});

// OpenAPI document + Scalar UI expose the full API surface and must not be served in Staging/Production.
if (app.Environment.IsDevelopment())
{
    app.UseOpenApi(c => c.Path = "/openapi/{documentName}.json");
    app.MapScalarApiReference();
}

app.UseNotificationModule();

await app.StartBeforeApplicationStartedHooksAsync();

app.UseHangfireModule();

app.Run();

void ConfigureLogging()
{
    Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
    builder.Host.UseSerilog(Log.Logger);
}

namespace Palladin.Api
{
    [UsedImplicitly]
    public partial class Program;
}
