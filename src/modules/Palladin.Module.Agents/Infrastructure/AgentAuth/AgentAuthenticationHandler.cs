using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Palladin.Core.Guid;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;

namespace Palladin.Module.Agents.Infrastructure.AgentAuth;

[UsedImplicitly]
internal sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AgentAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AgentsDbReadContext readContext,
    AgentsDbWriteContext writeContext,
    AgentsDomainWriteContext domainWriteContext,
    IMemoryCache cache,
    IOptions<ApiKeyCacheOptions> cacheOptions,
    IGuidProvider guidProvider,
    AgentSignatureVerifier signatureVerifier,
    IClock clock) : AuthenticationHandler<AgentAuthenticationOptions>(options, logger, encoder)
{
    private const int X25519PublicKeyBytes = 32;
    private const int Ed25519PublicKeyBytes = 32;
    private const int MaxAgentNameLength = 200;
    private const int MaxAgentTypeLength = 100;
    private const string DefaultAgentType = "Unknown";
    private const string CrossOrganizationKeyFailure = "Agent key registered to another organization";

    internal static string ApiKeyCacheKey(string keyHash) => $"apikey:{keyHash}";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AgentAuthenticationOptions.ApiKeyHeader, out var apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKeyPlaintext = apiKeyValues.ToString();
        if (string.IsNullOrWhiteSpace(apiKeyPlaintext))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = await FindActiveApiKeyAsync(ApiKey.HashKey(apiKeyPlaintext), Context.RequestAborted);
        if (apiKey is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!Request.Headers.TryGetValue(AgentAuthenticationOptions.AgentKeyHeader, out var agentKeyValues))
        {
            return AuthenticateResult.Fail("Missing agent key");
        }

        var agentPublicKey = agentKeyValues.ToString();
        if (!IsValidPublicKey(agentPublicKey))
        {
            return AuthenticateResult.Fail("Invalid agent key format");
        }

        var now = clock.GetCurrentInstant();
        var ip = NormalizeIp(Request.HttpContext.Connection.RemoteIpAddress);
        var hostname = ReadOptionalHeader(AgentAuthenticationOptions.AgentHostnameHeader);
        var name = ReadOptionalAgentName();
        var type = ReadAgentType();
        var signingPublicKey = ReadSigningPublicKey();
        var agent = await FindAgentByPublicKeyAsync(agentPublicKey, Context.RequestAborted);

        // The public key is a globally unique identity: reject one already enrolled under another organization.
        if (agent is not null && agent.OrganizationId != apiKey.OrganizationId)
        {
            return AuthenticateResult.Fail(CrossOrganizationKeyFailure);
        }

        if (agent is null)
        {
            if (signingPublicKey is null)
            {
                return AuthenticateResult.Fail("Missing or invalid signing key");
            }

            return await EnrollPendingAgentAsync(
                apiKey.OrganizationId,
                agentPublicKey,
                now,
                name,
                signingPublicKey,
                type ?? DefaultAgentType,
                ip,
                hostname,
                apiKey.ApiKeyId,
                Context.RequestAborted);
        }

        if (agent.Status != AgentStatus.Active
            && (agent.Status != AgentStatus.Deactivating || agent.AccessEpoch == 0))
        {
            var pendingName = agent.Name is null && agent.Status == AgentStatus.Pending ? name : null;
            ApplyConnectMetadata(agent, now, pendingName, type);
            await domainWriteContext.CommitAsync(Context.RequestAborted);

            Response.Headers[AgentAuthenticationOptions.AgentIdHeader] = agent.Id.ToString();
            return AuthenticateResult.Fail("Agent not active");
        }

        // Every request from an Active agent must prove possession of the signing key via a valid Ed25519
        // signature (a stolen API key + public key alone must not suffice).
        var signatureResult = await VerifyRequestSignatureAsync(agent.Id, agent.SigningPublicKey);
        if (signatureResult != AgentSignatureResult.Valid)
        {
            return AuthenticateResult.Fail($"Invalid agent signature ({signatureResult})");
        }

        if (agent.Status == AgentStatus.Active)
        {
            ApplyConnectMetadata(agent, now, null, type);
        }
        agent.RecordApiKeyUsage(apiKey.ApiKeyId, now, ip, hostname);
        await domainWriteContext.CommitAsync(Context.RequestAborted);

        return AuthenticateResult.Success(BuildTicket(agent.Id, agent.OrganizationId, agent.AccessEpoch));
    }

    private static void ApplyConnectMetadata(Agent agent, Instant now, string? name, string? type)
    {
        var nameChanged = name is not null && name.Trim() != agent.Name;
        var typeChanged = type is not null && type.Trim() != agent.Type;
        if (!nameChanged && !typeChanged)
        {
            return;
        }

        agent.UpdateOnConnect(now, nameChanged ? name : null, typeChanged ? type : null);
    }

    private async Task<AgentSignatureResult> VerifyRequestSignatureAsync(Guid agentId, string storedSigningKey)
    {
        Request.EnableBuffering();
        return await signatureVerifier.VerifyAsync(Request, agentId, storedSigningKey, Context.RequestAborted);
    }

    private string? ReadSigningPublicKey()
    {
        var value = ReadOptionalHeader(AgentAuthenticationOptions.AgentSigningKeyHeader);
        return value is not null && IsValidSigningPublicKey(value) ? value : null;
    }

    private string? ReadOptionalHeader(string headerName)
    {
        var value = Request.Headers[headerName].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string? ReadOptionalAgentName()
    {
        var name = ReadOptionalHeader(AgentAuthenticationOptions.AgentNameHeader);
        if (name is null)
        {
            return null;
        }

        return name.Length > MaxAgentNameLength ? name[..MaxAgentNameLength] : name;
    }

    private string? ReadAgentType()
    {
        var type = ReadOptionalHeader(AgentAuthenticationOptions.AgentTypeHeader);
        if (type is null)
        {
            return null;
        }

        return type.Length > MaxAgentTypeLength ? type[..MaxAgentTypeLength] : type;
    }

    private async Task<ResolvedApiKey?> FindActiveApiKeyAsync(string keyHash, CancellationToken ct)
    {
        var cacheKey = ApiKeyCacheKey(keyHash);
        if (cache.TryGetValue(cacheKey, out ResolvedApiKey? cached))
        {
            return cached;
        }

        var result = await readContext.ApiKeys
            .Where(x => x.KeyHash == keyHash && x.Status == ApiKeyStatus.Active)
            .Select(x => new ResolvedApiKey(x.Id, x.OrganizationId))
            .FirstOrDefaultAsync(ct);

        if (result is not null)
        {
            cache.Set(cacheKey, result, cacheOptions.Value.Duration);
        }

        return result;
    }

    private async Task<Agent?> FindAgentByPublicKeyAsync(string publicKey, CancellationToken ct) =>
        await writeContext.Agents
            .FirstOrDefaultAsync(x => x.PublicKey == publicKey, ct);

    // Loopback shows as IPv6 "::1" on a local host — store the friendlier 127.0.0.1.
    // IPv4-mapped IPv6 (::ffff:a.b.c.d) is unwrapped to its plain IPv4 form.
    private static string? NormalizeIp(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (IPAddress.IsLoopback(address))
        {
            return IPAddress.Loopback.ToString();
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    private async Task<AuthenticateResult> EnrollPendingAgentAsync(
        Guid organizationId,
        string publicKey,
        Instant now,
        string? name,
        string signingPublicKey,
        string type,
        string? ip,
        string? hostname,
        Guid apiKeyId,
        CancellationToken ct)
    {
        var agent = Agent.Create(guidProvider.Generate(), organizationId, publicKey, signingPublicKey, type, now, name, apiKeyId);
        agent.SetConnectionInfo(ip, hostname);
        domainWriteContext.Add(agent);

        try
        {
            await domainWriteContext.CommitAsync(ct);
            return PendingEnrollment(agent.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            domainWriteContext.Clear();
            return await ResolveConcurrentEnrollmentAsync(organizationId, publicKey, ct);
        }
    }

    // A concurrent enrollment won the race on the unique public-key index. Resolve to the winner only within
    // the SAME organization — returning a cross-org winner would hand back a foreign agent identity.
    private async Task<AuthenticateResult> ResolveConcurrentEnrollmentAsync(Guid organizationId, string publicKey, CancellationToken ct)
    {
        var winner = await readContext.Agents
            .Where(x => x.PublicKey == publicKey)
            .Select(x => new { x.Id, x.OrganizationId })
            .FirstAsync(ct);

        return winner.OrganizationId == organizationId
            ? PendingEnrollment(winner.Id)
            : AuthenticateResult.Fail(CrossOrganizationKeyFailure);
    }

    private AuthenticateResult PendingEnrollment(Guid agentId)
    {
        Response.Headers[AgentAuthenticationOptions.AgentIdHeader] = agentId.ToString();
        return AuthenticateResult.Fail("Agent pending enrollment");
    }

    private static bool IsValidPublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[X25519PublicKeyBytes];
        if (!Convert.TryFromBase64String(value, buffer, out var decodedLength))
        {
            return false;
        }

        return decodedLength == X25519PublicKeyBytes;
    }

    private static bool IsValidSigningPublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[Ed25519PublicKeyBytes];
        return Convert.TryFromBase64String(value, buffer, out var decodedLength)
               && decodedLength == Ed25519PublicKeyBytes;
    }

    private static AuthenticationTicket BuildTicket(Guid agentId, Guid organizationId, uint accessEpoch)
    {
        var claims = new[]
        {
            new Claim(AgentClaimNames.AgentId, agentId.ToString()),
            new Claim(AgentClaimNames.OrganizationId, organizationId.ToString()),
            new Claim(AgentClaimNames.AccessEpoch, accessEpoch.ToString(CultureInfo.InvariantCulture)),
        };

        var identity = new ClaimsIdentity(claims, AgentAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, AgentAuthenticationOptions.SchemeName);
    }
}
