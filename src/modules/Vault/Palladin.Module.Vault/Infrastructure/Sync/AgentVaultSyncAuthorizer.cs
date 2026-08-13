using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal sealed record AuthorizedAgentVault(
    Guid AgentId,
    Guid OrganizationId,
    Palladin.Module.Vault.Domain.Vault Vault,
    AgentVaultSyncAuthorizationStamp AuthorizationStamp);

internal sealed record AgentVaultSyncAuthorizationStamp(
    uint AgentAccessEpoch,
    uint AgentRecipientKeyVersion,
    Instant AgentUpdatedAt,
    uint VaultVdkVersion,
    uint VaultManifestSigningKeyVersion,
    uint VaultAgentMessageKeyVersion,
    ulong VaultDiscoverySequence,
    ulong VaultMinRetainedDiscoverySequence,
    Instant VaultUpdatedAt,
    ulong ManifestRevision,
    Instant EnvelopeProvisionedAt,
    string ManifestSignature);

internal static class AgentVaultSyncAuthorizer
{
    internal static async Task<AuthorizedAgentVault?> AcquireAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        Guid vaultId,
        VaultDomainReadContext readContext,
        CancellationToken ct)
    {
        var agentId = principal.GetAgentId();
        var organizationId = principal.GetAgentOrganizationId();
        var accessEpoch = principal.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            return null;
        }

        return await AcquireAsync(
            agentId.Value,
            organizationId.Value,
            accessEpoch.Value,
            vaultId,
            readContext,
            ct);
    }

    internal static async Task<bool> IsCurrentAsync(
        AuthorizedAgentVault authorization,
        VaultDomainReadContext readContext,
        CancellationToken ct)
    {
        var current = await AcquireAsync(
            authorization.AgentId,
            authorization.OrganizationId,
            authorization.AuthorizationStamp.AgentAccessEpoch,
            authorization.Vault.Id,
            readContext,
            ct);
        return current is not null && current.AuthorizationStamp == authorization.AuthorizationStamp;
    }

    private static async Task<AuthorizedAgentVault?> AcquireAsync(
        Guid agentId,
        Guid organizationId,
        uint accessEpoch,
        Guid vaultId,
        VaultDomainReadContext readContext,
        CancellationToken ct)
    {
        try
        {
            var agent = await readContext.Agents
                .Where(x => x.OrganizationId == organizationId && x.Id == agentId)
                .Where(x => x.Status == AgentStatus.Active && x.AccessEpoch == accessEpoch)
                .SingleOrDefaultAsync(ct);
            if (agent is null)
            {
                return null;
            }

            var vault = await readContext.Vaults
                .Where(x => x.OrganizationId == agent.OrganizationId && x.Id == vaultId)
                .SingleOrDefaultAsync(ct);
            if (vault is null)
            {
                return null;
            }

            var envelope = await readContext.AgentVaultDiscoveryEnvelopes
                .Where(x => x.OrganizationId == agent.OrganizationId
                            && x.VaultId == vault.Id
                            && x.AgentId == agent.Id)
                .SingleOrDefaultAsync(ct);
            var recipientKeyVersion = new AgentRecipientKeyVersion(agent.RecipientKeyVersion);
            if (envelope is null
                || envelope.RevokedAt is not null
                || envelope.RecipientAgentKeyVersion != recipientKeyVersion
                || envelope.ProvisionedAccessEpoch != agent.AccessEpoch
                || envelope.VdkVersion != vault.CurrentVdkVersion
                || envelope.ManifestSigningKeyVersion != vault.CurrentManifestSigningKeyVersion
                || envelope.AgentMessageKeyVersion != vault.CurrentAgentMessageKeyVersion)
            {
                return null;
            }

            var envelopeContract = VaultEnvelopeContractMapper.ToContract(envelope);
            var manifest = VaultEnvelopeContractMapper.ToManifestContract(envelope);
            _ = VaultManifestCryptoValidator.ValidateCurrent(envelopeContract, manifest, agent, vault);

            return new AuthorizedAgentVault(
                agent.Id,
                agent.OrganizationId,
                vault,
                new AgentVaultSyncAuthorizationStamp(
                    agent.AccessEpoch,
                    agent.RecipientKeyVersion,
                    agent.UpdatedAt,
                    vault.CurrentVdkVersion.Value,
                    vault.CurrentManifestSigningKeyVersion.Value,
                    vault.CurrentAgentMessageKeyVersion.Value,
                    vault.DiscoverySequence.Value,
                    vault.MinRetainedDiscoverySequence.Value,
                    vault.UpdatedAt,
                    envelope.ManifestRevision.Value,
                    envelope.ProvisionedAt,
                    Convert.ToBase64String(envelope.ManifestSignature)));
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
