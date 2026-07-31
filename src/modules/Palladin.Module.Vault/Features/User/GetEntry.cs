using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetEntryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
}

[PublicAPI]
public sealed record GetEntryResponse(
    Guid OrganizationId,
    Guid VaultId,
    Guid Id,
    EntryState State,
    string CurrentRevision,
    string MemberIndexRevision,
    string? AgentDiscoveryRevision,
    string AgentDiscoveryRevisionHighWatermark,
    uint CurrentKeyVersion,
    Instant CreatedAt,
    Guid CreatedBy,
    Instant UpdatedAt,
    Guid UpdatedBy,
    MemberIndexEnvelopeContract MemberIndex,
    MemberSecretEnvelopeContract MemberSecret,
    AgentDiscoveryEnvelopeContract? AgentDiscovery,
    VaultEntryKeyContract EntryKey);

[UsedImplicitly]
internal sealed class GetEntryValidator : Validator<GetEntryRequest>
{
    public GetEntryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class GetEntryEndpoint(VaultDomainReadContext domainReadContext) : Endpoint<GetEntryRequest, GetEntryResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/entries/{entryId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Get one canonical encrypted Entry";
            summary.Description = "Returns structural state plus the current MemberIndex, canonical MemberSecret version, optional AgentDiscovery projection and current wrapped Entry key. Presentation is decrypted only by the unlocked client.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(GetEntryRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var entry = await domainReadContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.VaultId == req.VaultId
                                      && x.Id == req.EntryId, ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var currentVersion = entry.Versions.Single(x => x.Revision == entry.CurrentRevision);
        var currentKey = entry.Keys.Single(x => x.KeyVersion == entry.CurrentKeyVersion);
        await Send.OkAsync(new GetEntryResponse(
            entry.OrganizationId,
            entry.VaultId,
            entry.Id,
            entry.State,
            entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
            entry.MemberIndexRevision.Value.ToString(CultureInfo.InvariantCulture),
            entry.AgentDiscoveryRevision?.Value.ToString(CultureInfo.InvariantCulture),
            entry.AgentDiscoveryRevisionHighWatermark.Value.ToString(CultureInfo.InvariantCulture),
            entry.CurrentKeyVersion.Value,
            entry.CreatedAt,
            entry.CreatedBy,
            entry.UpdatedAt,
            entry.UpdatedBy,
            VaultEnvelopeContractMapper.ToContract(entry.GetMemberIndex()),
            VaultEnvelopeContractMapper.ToContract(currentVersion.GetMemberSecret()),
            entry.GetAgentDiscovery() is { } discovery
                ? VaultEnvelopeContractMapper.ToContract(discovery)
                : null,
            VaultEnvelopeContractMapper.ToContract(currentKey)), ct);
    }
}
