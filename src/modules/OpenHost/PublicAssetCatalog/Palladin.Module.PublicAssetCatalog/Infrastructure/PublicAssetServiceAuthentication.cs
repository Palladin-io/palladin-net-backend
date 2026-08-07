using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure;

internal static class PublicAssetServiceAuthentication
{
    internal const string Scheme = "PublicAssetCatalogService";
    internal const string Role = "PublicAssetCatalogService";
    internal const string Issuer = "palladin-internal";
    internal const string Audience = "public-asset-catalog";

    internal static IServiceCollection AddPublicAssetServiceAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var secret = configuration["Modules:PublicAssetCatalog:ServiceAuth:SigningSecret"] ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Public Asset Catalog service signing secret must contain at least 32 UTF-8 bytes.");
        services.AddSingleton<ServiceJwtReplayGuard>();
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = Issuer,
                ValidateAudience = true, ValidAudience = Audience,
                ValidateLifetime = true, RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = "role",
                NameClaimType = "sub",
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var principal = context.Principal!;
                    var issuedAt = principal.FindFirst("iat")?.Value;
                    var expires = principal.FindFirst("exp")?.Value;
                    var jti = principal.FindFirst("jti")?.Value;
                    if (!string.Equals(principal.FindFirst("sub")?.Value, "agents", StringComparison.Ordinal)
                        || !long.TryParse(issuedAt, out var iat) || !long.TryParse(expires, out var exp)
                        || exp - iat is <= 0 or > 60 || jti is null
                        || !context.HttpContext.RequestServices.GetRequiredService<ServiceJwtReplayGuard>().TryConsume(jti, DateTimeOffset.FromUnixTimeSeconds(exp)))
                        context.Fail("Invalid or replayed service token.");
                    return Task.CompletedTask;
                },
            };
        });
        return services;
    }
}

internal sealed class ServiceJwtReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    internal bool TryConsume(string jti, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _seen.Where(x => x.Value <= now)) _seen.TryRemove(expired.Key, out _);
        return _seen.TryAdd(jti, expiresAt);
    }
}
