using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Core.Security;

namespace Palladin.Api.Framework;

internal static class RateLimitingExtensions
{
    // Generous fixed windows on brute-force / scraping surfaces only; everything else is unlimited.
    public static IServiceCollection AddPalladinRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path;
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // Auth is partitioned by IP: the caller is an anonymous browser with no API key yet.
                if (path.StartsWithSegments("/api/auth/oauth") || path.StartsWithSegments("/api/auth/refresh"))
                {
                    return FixedWindow($"auth:{ip}", permitLimit: 30);
                }

                if (path.StartsWithSegments("/api/auth/login"))
                {
                    return FixedWindow($"auth-login:{path}:{ip}", permitLimit: 30);
                }

                if (HttpMethods.IsPost(context.Request.Method)
                    && path.StartsWithSegments("/api/auth/shared-unlock/operations"))
                {
                    return FixedWindow($"shared-unlock-proof:{ip}", permitLimit: 60);
                }

                if (HttpMethods.IsPost(context.Request.Method)
                    && path.Equals(new PathString("/api/account/shared-unlock/operations")))
                {
                    var userId = context.User.GetUserId()?.ToString() ?? ip;
                    return FixedWindow($"shared-unlock-offer:{userId}", permitLimit: 30);
                }

                if (path.StartsWithSegments("/api/account/password")
                    || path.StartsWithSegments("/api/account/recovery")
                    || path.StartsWithSegments("/api/account/setup"))
                {
                    var userId = context.User.GetUserId()?.ToString() ?? ip;
                    return FixedWindow($"identity-security-write:{userId}", permitLimit: 5);
                }

                // Starting a pairing is anonymous by design, but each accepted request persists
                // bounded public metadata. Keep that write surface small per source address.
                if (HttpMethods.IsPost(context.Request.Method)
                    && AgentPairingRoute.IsStart(path))
                {
                    return FixedWindow($"agent-pairing-start:{ip}", permitLimit: 10);
                }


                // Status is anonymous until proof-of-possession can be checked against the
                // stored pairing key. Bound arbitrary-ID database reads before that lookup.
                if (HttpMethods.IsGet(context.Request.Method)
                    && AgentPairingRoute.IsStatus(path))
                {
                    return FixedWindow($"agent-pairing-status:{ip}", permitLimit: 120);
                }

                // Agent surfaces are partitioned by the org API key (SHA-256-hashed so the secret never lingers
                // in limiter state), falling back to IP when absent — fairer than per-IP behind a shared NAT.
                var apiKey = context.Request.Headers[AgentAuthenticationOptions.ApiKeyHeader].ToString();
                var agentPartition = string.IsNullOrEmpty(apiKey) ? $"ip:{ip}" : $"key:{HashApiKey(apiKey)}";

                if (path.StartsWithSegments("/api/agent/entries"))
                {
                    return FixedWindow($"discovery:{agentPartition}", permitLimit: 120);
                }

                if (path.StartsWithSegments("/api/agent/vaults"))
                {
                    return FixedWindow($"credential:{agentPartition}", permitLimit: 60);
                }

                return RateLimitPartition.GetNoLimiter("unlimited");
            });
        });

    private static string HashApiKey(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    private static RateLimitPartition<string> FixedWindow(string partitionKey, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}
