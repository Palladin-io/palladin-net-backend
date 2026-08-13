using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    Palladin.Module.Vault.Domain.Vault Vault);

internal sealed class AuthorizedAgentVaultLease(
    AuthorizedAgentVault access,
    IDbContextTransaction transaction) : IAsyncDisposable
{
    internal AuthorizedAgentVault Access { get; } = access;

    internal Task CompleteAsync(CancellationToken ct) => transaction.CommitAsync(ct);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}

internal static class AgentVaultSyncAuthorizer
{
    internal static async Task<AuthorizedAgentVaultLease?> AcquireAsync(
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

        var transaction = await readContext.BeginTransactionAsync(ct);
        try
        {
            // Lock every row that contributes to the access decision and retain the locks until
            // the response has been written. A concurrent status/epoch change, key rotation,
            // provisioning, revocation or deletion must therefore serialize before or after the
            // ciphertext delivery rather than racing between authorization and response.
            var agent = await readContext.LockAgentForShare(organizationId.Value, agentId.Value)
                .Where(x => x.Status == AgentStatus.Active && x.AccessEpoch == accessEpoch)
                .SingleOrDefaultAsync(ct);
            if (agent is null)
            {
                await transaction.DisposeAsync();
                return null;
            }

            var vault = await readContext.LockVaultForShare(agent.OrganizationId, vaultId)
                .SingleOrDefaultAsync(ct);
            if (vault is null)
            {
                await transaction.DisposeAsync();
                return null;
            }

            var envelope = await readContext.LockAgentDiscoveryEnvelopeForShare(
                    agent.OrganizationId,
                    vault.Id,
                    agent.Id)
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
                await transaction.DisposeAsync();
                return null;
            }

            // The authenticated Agent and its current provisioned envelope are the authorization
            // boundary. The runtime pins the first valid signed manifest and rejects later key or
            // revision regressions; a separate manual pairing ceremony is not required for sync.
            var envelopeContract = VaultEnvelopeContractMapper.ToContract(envelope);
            var manifest = VaultEnvelopeContractMapper.ToManifestContract(envelope);
            _ = VaultManifestCryptoValidator.Validate(envelopeContract, manifest, agent);

            return new AuthorizedAgentVaultLease(
                new AuthorizedAgentVault(agent.Id, agent.OrganizationId, vault),
                transaction);
        }
        catch (DomainException)
        {
            await transaction.DisposeAsync();
            return null;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
