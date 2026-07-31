using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ProvisionAgentDiscoveryRequest
{
    public Guid VaultId { get; init; }
    public Guid AgentId { get; init; }
    public AgentVaultDiscoveryEnvelopeContract Envelope { get; init; } = null!;
    public VaultManifestContract Manifest { get; init; } = null!;
}

[UsedImplicitly]
internal sealed class ProvisionAgentDiscoveryValidator : Validator<ProvisionAgentDiscoveryRequest>
{
    public ProvisionAgentDiscoveryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.Envelope).NotNull();
        RuleFor(x => x.Manifest).NotNull();
        When(x => x.Envelope is not null, () =>
        {
            RuleFor(x => x.Envelope.RecipientAgentKeyFingerprint).NotEmpty().Length(43);
            RuleFor(x => x.Envelope.AgentWrappedVdk)
                .NotEmpty()
                .MaximumLength(VaultProtocol.MaximumSealedVdkBase64UrlLength);
            RuleFor(x => x.Envelope.ManifestRevision).NotEmpty().MaximumLength(20);
            RuleFor(x => x.Envelope.ManifestSignature).NotEmpty().Length(86);
        });
        When(x => x.Manifest is not null, () =>
        {
            RuleFor(x => x.Manifest.AgentX25519Fingerprint).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.AgentEd25519Fingerprint).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.VaultSigningPublicKey).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.VaultSigningKeyFingerprint).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.VaultAgentMessagePublicKey).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.VaultAgentMessageKeyFingerprint).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.AgentWrappedVdkDigest).NotEmpty().Length(43);
            RuleFor(x => x.Manifest.ManifestRevision).NotEmpty().MaximumLength(20);
            RuleFor(x => x.Manifest.Signature).NotEmpty().Length(86);
        });
    }
}

[PublicAPI]
internal sealed class ProvisionAgentDiscoveryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ProvisionAgentDiscoveryRequest>
{
    public override void Configure()
    {
        Put("api/vaults/{VaultId:guid}/discovery/agents/{AgentId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        this.RequirePermission(Permission.VaultManage);
        Summary(summary =>
        {
            summary.Summary = "Provision a signed Vault Discovery manifest to an active Agent";
            summary.Description = "Stores a Member-created VDK envelope and signed manifest without exposing VDK. The manifest is bound to the Vault, both Agent identity keys and current key versions.";
        });
        Tags("Vault/Discovery");
    }

    public override async Task HandleAsync(ProvisionAgentDiscoveryRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await domainWriteContext.LockOrganizationAgentLifecycle(organizationId).SingleAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .Include(x => x.VaultMembers)
            .Include(x => x.AgentVaultDiscoveryEnvelopes)
            .Where(x => x.VaultMembers.Any(member => member.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Serialize provisioning with Agent deactivation. Both paths lock the canonical replica row,
        // so a higher manifest cannot clear a tombstone while a deactivation event is in flight.
        var agent = await domainWriteContext.LockAgent(organizationId, req.AgentId)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == req.AgentId, ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == req.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var provisioning = VaultManifestCryptoValidator.Validate(req.Envelope, req.Manifest, agent);
        vault.ProvisionAgentDiscovery(agent, provisioning, userId, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);
        await transaction.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
