using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Palladin.Core.Types;
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
public sealed record ListAgentVaultManifestsResponse(IReadOnlyList<AgentVaultManifestItem> Items);

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
            .Select(x => new { x.Id, x.OrganizationId, x.RecipientKeyVersion, x.AccessEpoch })
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var recipientKeyVersion = new AgentRecipientKeyVersion(agent.RecipientKeyVersion);
        var activationId = await domainReadContext.AgentPairingActivations
            .Where(x => x.OrganizationId == agent.OrganizationId
                        && x.AgentId == agent.Id
                        && x.AgentAccessEpoch == agent.AccessEpoch
                        && x.ConfirmedAt != null)
            .OrderByDescending(x => x.ConfirmedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (activationId is null)
        {
            await Send.OkAsync(new ListAgentVaultManifestsResponse([]), ct);
            return;
        }

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
            .Join(
                domainReadContext.AgentPairingActivationCandidates
                    .Where(candidate => candidate.ActivationId == activationId),
                envelope => new
                {
                    envelope.OrganizationId,
                    envelope.AgentId,
                    envelope.VaultId,
                    envelope.ManifestRevision,
                },
                candidate => new
                {
                    candidate.OrganizationId,
                    candidate.AgentId,
                    candidate.VaultId,
                    candidate.ManifestRevision,
                },
                (envelope, candidate) => new { Envelope = envelope, Candidate = candidate })
            .OrderBy(x => x.Envelope.VaultId)
            .Take(AgentPairingTranscriptService.MaximumCandidateVaults + 1)
            .ToListAsync(ct);
        if (envelopes.Count > AgentPairingTranscriptService.MaximumCandidateVaults)
        {
            AddError("agent-vault-manifest-limit-exceeded");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var items = new List<AgentVaultManifestItem>(envelopes.Count);
        foreach (var row in envelopes)
        {
            var manifest = VaultEnvelopeContractMapper.ToManifestContract(row.Envelope);
            var signedDigest = SHA256.HashData(VaultManifestCryptoValidator.CanonicalizeSigned(manifest));
            if (!CryptographicOperations.FixedTimeEquals(
                    row.Candidate.VaultSigningKeyFingerprint,
                    row.Envelope.VaultSigningKeyFingerprint)
                || !CryptographicOperations.FixedTimeEquals(
                    row.Candidate.SignedManifestDigest,
                    signedDigest))
            {
                AddError("agent-pairing-manifest-binding-mismatch");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            items.Add(new AgentVaultManifestItem(
                VaultEnvelopeContractMapper.ToContract(row.Envelope),
                manifest));
        }

        await Send.OkAsync(new ListAgentVaultManifestsResponse(items), ct);
    }
}
