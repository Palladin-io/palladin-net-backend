using FastEndpoints;
using FluentValidation;
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
public sealed record ListVaultsRequest { public int? Limit { get; init; } public int? Offset { get; init; } }

[PublicAPI]
public sealed record VaultSummary(
    Guid Id,
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
    Instant CreatedAt,
    Instant UpdatedAt,
    int MemberCount,
    int EntryCount,
    int ActiveGrantCount);

[PublicAPI]
public sealed record ListVaultsResponse(IReadOnlyList<VaultSummary> Vaults, int Total);

[UsedImplicitly]
internal sealed class ListVaultsValidator : Validator<ListVaultsRequest>
{
    public ListVaultsValidator()
    {
        RuleFor(x => x.Limit!.Value).InclusiveBetween(1, 200).When(x => x.Limit is not null);
        RuleFor(x => x.Offset!.Value).GreaterThanOrEqualTo(0).When(x => x.Offset is not null);
    }
}

[PublicAPI]
internal sealed class ListVaultsEndpoint(VaultDomainReadContext domainReadContext) : Endpoint<ListVaultsRequest, ListVaultsResponse>
{
    private const int DefaultLimit = 50;

    public override void Configure()
    {
        Get("api/vaults");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List encrypted Vault projections";
            summary.Description = "Returns only Vaults where the caller is a structural Member, with authenticated ciphertext and that Member's wrapped Vault key.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(ListVaultsRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var query = domainReadContext.Vaults
            .Where(v => v.OrganizationId == organizationId && v.VaultMembers.Any(m => m.UserId == userId));
        var total = await query.CountAsync(ct);
        var vaults = await query
            .OrderByDescending(v => v.CreatedAt)
            .Skip(req.Offset ?? 0)
            .Take(req.Limit ?? DefaultLimit)
            .ToListAsync(ct);

        var vaultIds = vaults.Select(v => v.Id).ToArray();
        var memberCounts = vaultIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await domainReadContext.VaultMembers
                .Where(member => member.OrganizationId == organizationId && vaultIds.Contains(member.VaultId))
                .GroupBy(member => member.VaultId)
                .ToDictionaryAsync(group => group.Key, group => group.Count(), ct);
        var entryCounts = vaultIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await domainReadContext.Entries
                .Where(e => e.OrganizationId == organizationId
                            && vaultIds.Contains(e.VaultId)
                            && e.State == Palladin.Core.Types.EntryState.Active)
                .GroupBy(e => e.VaultId)
                .ToDictionaryAsync(group => group.Key, group => group.Count(), ct);
        var memberKeys = vaultIds.Length == 0
            ? new Dictionary<Guid, Domain.VaultMemberKeyEnvelope>()
            : await domainReadContext.VaultMemberKeyEnvelopes
                .Where(key => key.OrganizationId == organizationId
                              && vaultIds.Contains(key.VaultId)
                              && key.MemberId == userId
                              && key.MemberKeyGeneration == key.Vault.MemberKeyGeneration)
                .ToDictionaryAsync(key => key.VaultId, ct);
        var keyMaterial = vaultIds.Length == 0
            ? new Dictionary<Guid, Domain.VaultKeyMaterialEnvelope[]>()
            : (await domainReadContext.VaultKeyMaterialEnvelopes
                    .Where(envelope => envelope.OrganizationId == organizationId
                                       && vaultIds.Contains(envelope.VaultId))
                    .OrderBy(envelope => envelope.VaultId)
                    .ThenBy(envelope => envelope.Kind)
                    .ToListAsync(ct))
                .GroupBy(envelope => envelope.VaultId)
                .ToDictionary(group => group.Key, group => group.ToArray());

        var summaries = new List<VaultSummary>(vaults.Count);
        foreach (var vault in vaults)
        {
            var memberKey = memberKeys[vault.Id];
            var vaultKeyMaterial = keyMaterial[vault.Id];
            summaries.Add(new VaultSummary(
                vault.Id,
                vault.IsDefault,
                vault.ProtocolVersion,
                vault.MemberSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                vault.DiscoverySequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                vault.MemberKeyGeneration.Value,
                VaultEnvelopeContractMapper.ToContract(vault.CurrentKeyEpoch),
                VaultEnvelopeContractMapper.ToContract(vault.GetMemberVaultMetadata()),
                VaultEnvelopeContractMapper.ToContract(memberKey.GetWrappedVaultKey()),
                VaultEnvelopeContractMapper.ToDiscoveryKeyContract(
                    vaultKeyMaterial.Single(x => x.Kind == Domain.VaultKeyMaterialKind.DiscoveryKey)),
                vaultKeyMaterial
                    .Where(x => x.Kind != Domain.VaultKeyMaterialKind.DiscoveryKey)
                    .Select(VaultEnvelopeContractMapper.ToPrivateKeyContract)
                    .ToArray(),
                vault.CreatedAt,
                vault.UpdatedAt,
                memberCounts.GetValueOrDefault(vault.Id),
                entryCounts.GetValueOrDefault(vault.Id),
                0));
        }

        await Send.OkAsync(new ListVaultsResponse(summaries, total), ct);
    }
}
