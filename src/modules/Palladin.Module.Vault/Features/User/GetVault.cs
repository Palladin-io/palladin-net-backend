using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetVaultRequest { public Guid Id { get; init; } }

[PublicAPI]
public sealed record GetVaultResponse(
    Guid Id,
    Guid OrganizationId,
    bool IsDefault,
    ushort ProtocolVersion,
    string MemberSequence,
    string DiscoverySequence,
    uint MemberKeyGeneration,
    VaultKeyEpochContract CurrentKeyEpoch,
    MemberVaultMetadataEnvelopeContract MemberVaultMetadata,
    MemberVaultKeyEnvelopeContract MemberVaultKey,
    VaultDiscoveryKeyEnvelopeContract DiscoveryKey,
    IReadOnlyList<VaultPrivateKeyEnvelopeContract> VaultPrivateKeys,
    VaultPublicKeyContract VaultAgentMessagePublicKey,
    VaultPublicKeyContract VaultManifestSigningPublicKey,
    Instant CreatedAt,
    Instant UpdatedAt,
    int MemberCount,
    int EntryCount,
    int ActiveGrantCount);

[PublicAPI]
internal sealed class GetVaultEndpoint(VaultDomainReadContext domainReadContext) : Endpoint<GetVaultRequest, GetVaultResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{id:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Get an encrypted Vault projection";
            summary.Description = "Returns authenticated ciphertext and the caller-specific wrapped Vault key only to a structural Vault Member. Organization administrators cannot bypass Vault membership.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(GetVaultRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var organizationId = User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var vault = await domainReadContext.Vaults
            .Include(v => v.VaultMembers)
            .Include(v => v.VaultMemberKeyEnvelopes)
            .Include(v => v.KeyMaterialEnvelopes)
            .Where(v => v.OrganizationId == organizationId && v.Id == req.Id)
            .Where(v => v.VaultMembers.Any(m => m.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var memberKey = vault.VaultMemberKeyEnvelopes.Single(x =>
            x.MemberId == userId && x.MemberKeyGeneration == vault.MemberKeyGeneration);
        var entryCount = await domainReadContext.Entries.CountAsync(
            e => e.OrganizationId == organizationId
                 && e.VaultId == vault.Id
                 && e.State == Palladin.Core.Types.EntryState.Active,
            ct);

        await Send.OkAsync(new GetVaultResponse(
            vault.Id,
            vault.OrganizationId,
            vault.IsDefault,
            vault.ProtocolVersion,
            vault.MemberSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            vault.DiscoverySequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            vault.MemberKeyGeneration.Value,
            VaultEnvelopeContractMapper.ToContract(vault.CurrentKeyEpoch),
            VaultEnvelopeContractMapper.ToContract(vault.GetMemberVaultMetadata()),
            VaultEnvelopeContractMapper.ToContract(memberKey.GetWrappedVaultKey()),
            VaultEnvelopeContractMapper.ToDiscoveryKeyContract(
                vault.KeyMaterialEnvelopes.Single(x => x.Kind == Domain.VaultKeyMaterialKind.DiscoveryKey)),
            vault.KeyMaterialEnvelopes
                .Where(x => x.Kind != Domain.VaultKeyMaterialKind.DiscoveryKey)
                .OrderBy(x => x.Kind)
                .Select(VaultEnvelopeContractMapper.ToPrivateKeyContract)
                .ToArray(),
            VaultEnvelopeContractMapper.ToAgentMessagePublicKeyContract(vault),
            VaultEnvelopeContractMapper.ToManifestSigningPublicKeyContract(vault),
            vault.CreatedAt,
            vault.UpdatedAt,
            vault.VaultMembers.Count,
            entryCount,
            0), ct);
    }
}
