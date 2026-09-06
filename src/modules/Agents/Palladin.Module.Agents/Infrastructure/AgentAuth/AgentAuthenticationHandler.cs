using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Palladin.Core.Guid;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.Persistence.Configurations;
using Palladin.Module.Agents.Shared;
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
    AgentDisplayNameCoordinator displayNameCoordinator,
    IMemoryCache cache,
    IOptions<ApiKeyCacheOptions> cacheOptions,
    IGuidProvider guidProvider,
    AgentSignatureVerifier signatureVerifier,
    IClock clock) : AuthenticationHandler<AgentAuthenticationOptions>(options, logger, encoder)
{
    private const string CrossOrganizationKeyFailure = "Agent key registered to another organization";

    internal static string ApiKeyCacheKey(string keyHash) => $"apikey:{keyHash}";

    internal static void InvalidateApiKeyCache(IMemoryCache cache, string keyHash) =>
        cache.Remove(ApiKeyCacheKey(keyHash));

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

        if (!AgentPublicKey.TryNormalize(agentKeyValues.ToString(), out var agentPublicKey))
        {
            return AuthenticateResult.Fail("Invalid agent key format");
        }

        var now = clock.GetCurrentInstant();
        var ip = NormalizeIp(Request.HttpContext.Connection.RemoteIpAddress);
        var hostname = ReadOptionalHeader(AgentAuthenticationOptions.AgentHostnameHeader);
        if (!TryReadAgentMetadata(out var name, out var type))
        {
            return AuthenticateResult.Fail("Invalid agent metadata");
        }
        var signingPublicKey = ReadSigningPublicKey();
        var agent = await FindAgentByPublicKeyAsync(agentPublicKey, Context.RequestAborted);

        if (apiKey.AgentId is not null && agent?.Id != apiKey.AgentId)
        {
            return AuthenticateResult.Fail("API key credential is bound to another Agent");
        }

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
                type,
                ip,
                hostname,
                apiKey.ApiKeyId,
                Context.RequestAborted);
        }

        if (agent.Status != AgentStatus.Active
            && (agent.Status != AgentStatus.Deactivating || agent.AccessEpoch == 0))
        {
            var pendingName = agent.Name is null && agent.Status == AgentStatus.Pending ? name : null;
            if (pendingName is not null)
            {
                await displayNameCoordinator.FenceAsync(agent.OrganizationId, Context.RequestAborted);
                if (!await displayNameCoordinator.IsAvailableAsync(
                        agent.OrganizationId, pendingName, now, agent.Id, null, Context.RequestAborted))
                {
                    pendingName = null;
                }
            }

            ApplyConnectMetadata(agent, now, pendingName, type);
            try
            {
                await domainWriteContext.CommitAsync(Context.RequestAborted);
            }
            catch (DbUpdateConcurrencyException)
            {
                domainWriteContext.Clear();
                return AuthenticateResult.Fail("Concurrent Agent metadata update; retry the request");
            }
            catch (DbUpdateException ex) when (IsDisplayNameFenceCreationConflict(ex))
            {
                domainWriteContext.Clear();
                return AuthenticateResult.Fail("Concurrent Agent metadata update; retry the request");
            }

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
        return AgentPublicKey.TryNormalize(value, out var normalized) ? normalized : null;
    }

    private string? ReadOptionalHeader(string headerName)
    {
        var value = Request.Headers[headerName].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private bool TryReadAgentMetadata(out string? name, out string? type)
    {
        type = null;
        if (!AgentMetadata.TryNormalizeDisplayName(
                ReadOptionalHeader(AgentAuthenticationOptions.AgentNameHeader), out name))
        {
            return false;
        }

        return AgentMetadata.TryNormalizeType(
            ReadOptionalHeader(AgentAuthenticationOptions.AgentTypeHeader), out type);
    }

    private async Task<ResolvedApiKey?> FindActiveApiKeyAsync(string keyHash, CancellationToken ct)
    {
        var cacheKey = ApiKeyCacheKey(keyHash);
        if (cache.TryGetValue(cacheKey, out ResolvedApiKey? cached))
        {
            return cached;
        }

        // Hidden child credentials are deliberately never cached. Resolving their logical parent
        // from authoritative storage on every request makes a committed parent revoke/delete take
        // effect across every API replica without a distributed cache-invalidation dependency.
        var child = await readContext.ApiKeyCredentials
            .Where(x => x.KeyHash == keyHash)
            .Join(
                readContext.ApiKeys.Where(x => x.Status == ApiKeyStatus.Active),
                credential => credential.ApiKeyId,
                apiKey => apiKey.Id,
                (credential, apiKey) => new ResolvedApiKey(apiKey.Id, apiKey.OrganizationId, credential.AgentId))
            .FirstOrDefaultAsync(ct);
        if (child is not null)
        {
            return child;
        }

        var result = await readContext.ApiKeys
            .Where(x => x.KeyHash == keyHash && x.Status == ApiKeyStatus.Active)
            .Select(x => new ResolvedApiKey(x.Id, x.OrganizationId, null))
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
        string? type,
        string? ip,
        string? hostname,
        Guid apiKeyId,
        CancellationToken ct)
    {
        if (name is not null)
        {
            await displayNameCoordinator.FenceAsync(organizationId, ct);
            if (!await displayNameCoordinator.IsAvailableAsync(
                    organizationId, name, now, null, null, ct))
            {
                name = null;
            }
        }

        var agent = Agent.Create(guidProvider.Generate(), organizationId, publicKey, signingPublicKey, type, now, name, apiKeyId);
        agent.SetConnectionInfo(ip, hostname);
        domainWriteContext.Add(agent);

        try
        {
            await domainWriteContext.CommitAsync(ct);
            return PendingEnrollment(agent.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            return await EnrollPendingAgentAsync(
                organizationId, publicKey, now, null, signingPublicKey, type, ip, hostname, apiKeyId, ct);
        }
        catch (DbUpdateException ex) when (IsDisplayNameFenceCreationConflict(ex))
        {
            domainWriteContext.Clear();
            return await EnrollPendingAgentAsync(
                organizationId, publicKey, now, null, signingPublicKey, type, ip, hostname, apiKeyId, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            domainWriteContext.Clear();
            return await ResolveConcurrentEnrollmentAsync(organizationId, publicKey, ct);
        }
    }

    private static bool IsDisplayNameFenceCreationConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AgentDisplayNameFenceConfiguration.PrimaryKey,
        };

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
