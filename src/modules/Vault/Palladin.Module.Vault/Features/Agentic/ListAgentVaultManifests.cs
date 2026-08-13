using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record AgentVaultManifestItem(
    AgentVaultDiscoveryEnvelopeContract Envelope,
    VaultManifestContract Manifest);

[PublicAPI]
public sealed record ListAgentVaultManifestsResponse(
    uint AgentAccessEpoch,
    IReadOnlyList<AgentVaultManifestItem> Items);

[PublicAPI]
internal sealed class ListAgentVaultManifestsEndpoint(VaultDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListAgentVaultManifestsResponse>
{
    private const string ProtocolHeader = "X-Palladin-Vault-Protocol";

    public override void Configure()
    {
        Get("api/agent/vault-manifests");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "List current signed Vault manifests for an active Agent";
            summary.Description = "Returns only current VDK envelopes provisioned to the concrete active Agent authenticated by organization API key and Ed25519 request proof.";
        });
        Tags("Vault/Discovery");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Request.Headers[ProtocolHeader].ToString() != VaultProtocol.CurrentVersion.ToString())
        {
            AddError("unsupported-protocol");
            await Send.ErrorsAsync(426, ct);
            return;
        }

        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(x => x.Id == agentId
                        && x.OrganizationId == organizationId
                        && x.Status == AgentStatus.Active
                        && x.AccessEpoch == accessEpoch)
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var recipientKeyVersion = new AgentRecipientKeyVersion(agent.RecipientKeyVersion);
        var envelopes = await domainReadContext.AgentVaultDiscoveryEnvelopes
            .Where(x => x.OrganizationId == agent.OrganizationId && x.AgentId == agent.Id)
            .Where(x => x.RevokedAt == null)
            .Where(x => x.RecipientAgentKeyVersion == recipientKeyVersion)
            .Where(x => x.ProvisionedAccessEpoch == agent.AccessEpoch)
            .Where(x => domainReadContext.Vaults.Any(vault =>
                vault.OrganizationId == x.OrganizationId
                && vault.Id == x.VaultId
                && vault.CurrentVdkVersion == x.VdkVersion
                && vault.CurrentManifestSigningKeyVersion == x.ManifestSigningKeyVersion
                && vault.CurrentAgentMessageKeyVersion == x.AgentMessageKeyVersion))
            .OrderBy(x => x.VaultId)
            .Take(AgentPairingTranscriptService.MaximumCandidateVaults + 1)
            .ToListAsync(ct);
        if (envelopes.Count > AgentPairingTranscriptService.MaximumCandidateVaults)
        {
            AddError("agent-vault-manifest-limit-exceeded");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var vaultIds = envelopes.Select(x => x.VaultId).ToArray();
        var vaults = await domainReadContext.Vaults
            .Where(x => x.OrganizationId == agent.OrganizationId && vaultIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var items = new List<AgentVaultManifestItem>(envelopes.Count);
        try
        {
            foreach (var envelope in envelopes)
            {
                if (!vaults.TryGetValue(envelope.VaultId, out var vault))
                {
                    throw new DomainException("Vault manifest references a missing Vault.");
                }

                var envelopeContract = VaultEnvelopeContractMapper.ToContract(envelope);
                var manifest = VaultEnvelopeContractMapper.ToManifestContract(envelope);
                _ = VaultManifestCryptoValidator.ValidateCurrent(envelopeContract, manifest, agent, vault);

                items.Add(new AgentVaultManifestItem(
                    envelopeContract,
                    manifest));
            }
        }
        catch (DomainException)
        {
            AddError("agent-vault-manifest-invalid");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.OkAsync(new ListAgentVaultManifestsResponse(agent.AccessEpoch, items), ct);
    }
}
