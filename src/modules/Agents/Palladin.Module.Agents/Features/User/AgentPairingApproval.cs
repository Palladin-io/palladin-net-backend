using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Guid;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Pairing;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

namespace Palladin.Module.Agents.Features;

internal sealed class AgentPairingApproval(
    AgentsDomainWriteContext domainWriteContext,
    AgentDisplayNameCoordinator displayNameCoordinator,
    AgentPairingCredentialProtector credentialProtector,
    IGuidProvider guidProvider,
    IClock clock)
{
    private static readonly HashSet<string> PairingConflictConstraints =
    [
        "IX_agents_public_key",
        "IX_api_key_credentials_agent_id",
        "IX_api_key_credentials_key_hash",
        AgentDisplayNameFenceConfiguration.PrimaryKey,
    ];

    public async Task<(int StatusCode, Guid? AgentId)> ApproveAsync(
        Guid pairingId, string requestedDisplayName, string? iconKey,
        Guid organizationId, Guid userId, ApiKey apiKey, CancellationToken ct)
    {
        _ = AgentMetadata.TryNormalizeRequiredDisplayName(requestedDisplayName, out var displayName);
        var pairing = await domainWriteContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == pairingId, ct);
        var now = clock.GetCurrentInstant();
        if (pairing is null
            || pairing.OrganizationId != organizationId
            || pairing.Status != AgentPairingStatus.Pending
            || pairing.IsExpired(now)
            || !AgentMetadata.TryNormalizeType(pairing.Type, out var normalizedType))
        {
            return (StatusCodes.Status404NotFound, null);
        }

        await displayNameCoordinator.FenceAsync(organizationId, ct);
        if (!await displayNameCoordinator.IsAvailableAsync(
                organizationId, displayName, now, null, pairing.Id, ct))
        {
            return (StatusCodes.Status409Conflict, null);
        }

        var agentId = guidProvider.Generate();
        using var credentialSecret = GeneratedApiKeySecret.Generate();
        var credential = ApiKeyCredential.FromPlaintext(
            guidProvider.Generate(), apiKey.Id, agentId, credentialSecret.Bytes, now);
        var envelope = credentialProtector.Protect(
            pairing.Id,
            organizationId,
            agentId,
            apiKey.Id,
            pairing.PublicKey,
            credentialSecret.Bytes);
        var agent = Agent.Create(
            agentId,
            organizationId,
            pairing.PublicKey,
            pairing.SigningPublicKey,
            normalizedType,
            now,
            displayName,
            apiKey.Id);
        agent.Activate(userId, now, displayName, normalizedType, iconKey, null);
        agent.RecordBrowserPairingEnrollment();
        if (!pairing.TryApprove(organizationId, agentId, apiKey.Id, displayName, normalizedType, envelope, now))
        {
            return (StatusCodes.Status409Conflict, null);
        }

        domainWriteContext.Add(agent);
        domainWriteContext.Add(credential);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            return (StatusCodes.Status409Conflict, null);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } constraintName,
        } && PairingConflictConstraints.Contains(constraintName))
        {
            domainWriteContext.Clear();
            return (StatusCodes.Status409Conflict, null);
        }

        return (StatusCodes.Status200OK, agent.Id);
    }
}
