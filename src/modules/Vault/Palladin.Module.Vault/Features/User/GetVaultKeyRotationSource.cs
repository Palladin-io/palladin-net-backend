using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record VaultKeyRotationSourceRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid RotationId { get; init; }
    public Guid FencingToken { get; init; }
    public Guid? AfterId { get; init; }
    public uint? AfterVersion { get; init; }
    public int PageSize { get; init; } = 50;
}

[UsedImplicitly]
internal sealed class VaultKeyRotationSourceValidator : Validator<VaultKeyRotationSourceRequest>
{
    public VaultKeyRotationSourceValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.RotationId).NotEmpty();
        RuleFor(x => x.FencingToken).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x).Must(x => x.AfterId is not null || x.AfterVersion is null)
            .WithMessage("An entry key version cursor requires an entry identifier cursor.");
    }
}

[PublicAPI]
public sealed record RotationMemberRecipientContract(
    Guid MemberId,
    uint RecipientKeyVersion,
    string RecipientKeyFingerprint,
    string X25519PublicKey);

[PublicAPI]
public sealed record VaultKeyRotationMemberSourceResponse(
    IReadOnlyList<RotationMemberRecipientContract> Items,
    Guid? NextAfterId);

[PublicAPI]
public sealed record RotationFullGrantRecipientContract(
    Guid GrantId,
    Guid AgentId,
    uint AgentAccessEpoch,
    uint RecipientKeyVersion,
    string RecipientKeyFingerprint,
    string X25519PublicKey);

[PublicAPI]
public sealed record VaultKeyRotationFullGrantSourceResponse(
    IReadOnlyList<RotationFullGrantRecipientContract> Items,
    Guid? NextAfterId);

[PublicAPI]
internal sealed class GetVaultKeyRotationFullGrantSourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock) : Endpoint<VaultKeyRotationSourceRequest, VaultKeyRotationFullGrantSourceResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/source/full-grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(x => x.Description = "Returns a bounded page of active FULL-grant Agent recipients that require the target VK wrapper.");
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(VaultKeyRotationSourceRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var rotation = await RotationSourceAuthorization.LoadAsync(
            domainReadContext, organizationId, req, userId, clock.GetCurrentInstant(), ct);
        var query =
            from grant in domainReadContext.Grants.OfType<FullGrant>()
            join agent in domainReadContext.Agents on grant.AgentId equals agent.Id
            where grant.OrganizationId == organizationId
                  && grant.VaultId == req.VaultId
                  && grant.Status == GrantStatus.Active
                  && agent.OrganizationId == organizationId
                  && agent.Status == AgentStatus.Active
                  && agent.AccessEpoch == grant.AgentAccessEpoch
                  && agent.Id != rotation.ExcludedAgentId
            select new
            {
                GrantId = grant.Id,
                grant.AgentId,
                grant.AgentAccessEpoch,
                agent.RecipientKeyVersion,
                agent.PublicKey,
            };
        if (req.AfterId is { } after)
        {
            query = query.Where(x => x.GrantId.CompareTo(after) > 0);
        }

        var page = await query.OrderBy(x => x.GrantId).Take(req.PageSize).ToListAsync(ct);
        await Send.OkAsync(new VaultKeyRotationFullGrantSourceResponse(
            page.Select(x =>
            {
                var publicKey = Convert.FromBase64String(x.PublicKey);
                return new RotationFullGrantRecipientContract(
                    x.GrantId,
                    x.AgentId,
                    x.AgentAccessEpoch,
                    x.RecipientKeyVersion,
                    WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(publicKey, VaultKeyKind.AgentX25519)),
                    WebEncoders.Base64UrlEncode(publicKey));
            }).ToArray(),
            page.Count == req.PageSize ? page[^1].GrantId : null), ct);
    }
}

[PublicAPI]
public sealed record VaultKeyRotationPublicKeySourceResponse(
    VaultPublicKeyContract VaultAgentMessagePublicKey,
    VaultPublicKeyContract VaultManifestSigningPublicKey);

[PublicAPI]
internal sealed class GetVaultKeyRotationPublicKeySourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock) : Endpoint<VaultKeyRotationSourceRequest, VaultKeyRotationPublicKeySourceResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/source/public-keys");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(x => x.Description = "Returns the current versioned Vault public trust anchors for a fenced rotation.");
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(VaultKeyRotationSourceRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await RotationSourceAuthorization.LoadAsync(
            domainReadContext, organizationId, req, userId, clock.GetCurrentInstant(), ct);
        var vault = await domainReadContext.Vaults.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == organizationId && x.Id == req.VaultId, ct);
        await Send.OkAsync(new VaultKeyRotationPublicKeySourceResponse(
            VaultEnvelopeContractMapper.ToAgentMessagePublicKeyContract(vault),
            VaultEnvelopeContractMapper.ToManifestSigningPublicKeyContract(vault)), ct);
    }
}

[PublicAPI]
internal sealed class GetVaultKeyRotationMemberSourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock) : Endpoint<VaultKeyRotationSourceRequest, VaultKeyRotationMemberSourceResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/source/members");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(x => x.Description = "Returns one bounded, exact remaining-Member recipient page for the active fenced rotation lease.");
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(VaultKeyRotationSourceRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var rotation = await RotationSourceAuthorization.LoadAsync(
            domainReadContext, organizationId, req, userId, clock.GetCurrentInstant(), ct);
        var query =
            from member in domainReadContext.VaultMembers
            join directory in domainReadContext.MemberKeyDirectory on member.UserId equals directory.UserId
            where member.OrganizationId == organizationId && member.VaultId == req.VaultId
                  && member.UserId != rotation.ExcludedMemberId
            select new { member.UserId, directory.KeyVersion, directory.Fingerprint, directory.PublicKey };
        if (req.AfterId is { } after)
        {
            query = query.Where(x => x.UserId.CompareTo(after) > 0);
        }
        var page = await query.OrderBy(x => x.UserId).Take(req.PageSize).ToListAsync(ct);
        await Send.OkAsync(new VaultKeyRotationMemberSourceResponse(
            page.Select(x => new RotationMemberRecipientContract(
                x.UserId,
                x.KeyVersion.Value,
                WebEncoders.Base64UrlEncode(x.Fingerprint),
                WebEncoders.Base64UrlEncode(x.PublicKey))).ToArray(),
            page.Count == req.PageSize ? page[^1].UserId : null), ct);
    }
}

[PublicAPI]
public sealed record VaultKeyRotationEntryKeySourceResponse(
    IReadOnlyList<VaultEntryKeyContract> Items,
    Guid? NextAfterId,
    uint? NextAfterVersion);

[PublicAPI]
public sealed record VaultKeyRotationEntryMemberHeadSourceRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid RotationId { get; init; }
    public Guid FencingToken { get; init; }
    public Guid? AfterId { get; init; }
    public int PageSize { get; init; } = 10;
}

[UsedImplicitly]
internal sealed class VaultKeyRotationEntryMemberHeadSourceValidator
    : Validator<VaultKeyRotationEntryMemberHeadSourceRequest>
{
    public VaultKeyRotationEntryMemberHeadSourceValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.RotationId).NotEmpty();
        RuleFor(x => x.FencingToken).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 10);
    }
}

[PublicAPI]
public sealed record RotationEntryMemberHeadSourceContract(
    Guid EntryId,
    string CurrentRevision,
    uint CurrentKeyVersion,
    MemberIndexEnvelopeContract MemberIndex,
    MemberSecretEnvelopeContract MemberSecret);

[PublicAPI]
public sealed record VaultKeyRotationEntryMemberHeadSourceResponse(
    IReadOnlyList<RotationEntryMemberHeadSourceContract> Items,
    Guid? NextAfterId);

[PublicAPI]
internal sealed class GetVaultKeyRotationEntryMemberHeadSourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock)
    : Endpoint<VaultKeyRotationEntryMemberHeadSourceRequest,
        VaultKeyRotationEntryMemberHeadSourceResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/source/entry-member-heads");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(x => x.Description =
            "Returns a bounded page of complete current Member Entry heads for target-generation reencryption.");
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(
        VaultKeyRotationEntryMemberHeadSourceRequest req,
        CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await RotationSourceAuthorization.LoadAsync(
            domainReadContext,
            organizationId,
            req.VaultId,
            req.RotationId,
            req.FencingToken,
            userId,
            clock.GetCurrentInstant(),
            ct);

        var query = domainReadContext.Entries.Where(x =>
            x.OrganizationId == organizationId && x.VaultId == req.VaultId);
        if (req.AfterId is { } after)
        {
            query = query.Where(x => x.Id.CompareTo(after) > 0);
        }

        var entries = await query.OrderBy(x => x.Id).Take(req.PageSize).ToListAsync(ct);
        var entryIds = entries.Select(x => x.Id).ToArray();
        var currentVersions = entryIds.Length == 0
            ? new Dictionary<Guid, VaultEntryVersion>()
            : await (
                from version in domainReadContext.EntryVersions
                join entry in domainReadContext.Entries
                    on new { version.OrganizationId, version.VaultId, version.EntryId }
                    equals new { entry.OrganizationId, entry.VaultId, EntryId = entry.Id }
                where entry.OrganizationId == organizationId
                      && entry.VaultId == req.VaultId
                      && entryIds.Contains(entry.Id)
                      && version.Revision == entry.CurrentRevision
                select version)
                .ToDictionaryAsync(x => x.EntryId, ct);

        await Send.OkAsync(new VaultKeyRotationEntryMemberHeadSourceResponse(
            entries.Select(entry =>
            {
                var version = currentVersions[entry.Id];
                return new RotationEntryMemberHeadSourceContract(
                    entry.Id,
                    entry.CurrentRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    entry.CurrentKeyVersion.Value,
                    VaultEnvelopeContractMapper.ToContract(entry.GetMemberIndex()),
                    VaultEnvelopeContractMapper.ToContract(version.GetMemberSecret()));
            }).ToArray(),
            entries.Count == req.PageSize ? entries[^1].Id : null), ct);
    }
}

[PublicAPI]
internal sealed class GetVaultKeyRotationEntryKeySourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock) : Endpoint<VaultKeyRotationSourceRequest, VaultKeyRotationEntryKeySourceResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/source/entry-keys");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(x => x.Description = "Returns a keyset-paginated, no-tracking page of current retained Entry key wrappers.");
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(VaultKeyRotationSourceRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await RotationSourceAuthorization.LoadAsync(
            domainReadContext, organizationId, req, userId, clock.GetCurrentInstant(), ct);
        var page = await domainReadContext.GetRotationEntryKeyPage(
            organizationId, req.VaultId, req.AfterId, req.AfterVersion, req.PageSize).ToListAsync(ct);
        var last = page.LastOrDefault();
        await Send.OkAsync(new VaultKeyRotationEntryKeySourceResponse(
            page.Select(x => new VaultEntryKeyContract(
                new EnvelopeDescriptorContract<VaultKeyEnvelopeBindingContract>(
                    checked((ushort)x.ProtocolVersion),
                    x.CryptoSuiteId,
                    EnvelopePurposeContract.EntryDekByVaultKey,
                    new EnvelopeScopeContract(x.OrganizationId, x.VaultId, x.EntryId),
                    x.WrapperRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    checked((uint)x.KeyVersion),
                    checked((uint)x.MemberKeyGeneration),
                    new VaultKeyEnvelopeBindingContract(checked((uint)x.WrappingKeyVersion))),
                WebEncoders.Base64UrlEncode(x.EncodedSuitePayload))).ToArray(),
            page.Count == req.PageSize ? last!.EntryId : null,
            page.Count == req.PageSize ? checked((uint)last!.KeyVersion) : null), ct);
    }
}

[PublicAPI]
public sealed record RotationEntryDiscoverySourceContract(
    string SourceRevision,
    AgentDiscoveryEnvelopeContract Envelope);

[PublicAPI]
public sealed record VaultKeyRotationDiscoverySourceResponse(
    IReadOnlyList<RotationEntryDiscoverySourceContract> Items,
    Guid? NextAfterId);

[PublicAPI]
internal sealed class GetVaultKeyRotationDiscoverySourceEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock) : Endpoint<VaultKeyRotationSourceRequest, VaultKeyRotationDiscoverySourceResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/key-rotations/{rotationId:guid}/source/discoveries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(x => x.Description = "Returns a keyset-paginated, no-tracking page of current encrypted Agent Discovery projections.");
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(VaultKeyRotationSourceRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await RotationSourceAuthorization.LoadAsync(
            domainReadContext, organizationId, req, userId, clock.GetCurrentInstant(), ct);
        var query = domainReadContext.Entries.Where(x =>
            x.OrganizationId == organizationId && x.VaultId == req.VaultId
            && x.AgentDiscoveryRevision != null);
        if (req.AfterId is { } after)
        {
            query = query.Where(x => x.Id.CompareTo(after) > 0);
        }
        var page = await query.OrderBy(x => x.Id).Take(req.PageSize).ToListAsync(ct);
        await Send.OkAsync(new VaultKeyRotationDiscoverySourceResponse(
            page.Select(x =>
            {
                var discovery = x.GetAgentDiscovery()!;
                return new RotationEntryDiscoverySourceContract(
                    discovery.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    VaultEnvelopeContractMapper.ToContract(discovery));
            }).ToArray(),
            page.Count == req.PageSize ? page[^1].Id : null), ct);
    }
}

internal static class RotationSourceAuthorization
{
    internal static async Task<VaultKeyRotation> LoadAsync(
        VaultDomainReadContext context,
        Guid organizationId,
        VaultKeyRotationSourceRequest request,
        Guid memberId,
        Instant now,
        CancellationToken cancellationToken) => await LoadAsync(
        context,
        organizationId,
        request.VaultId,
        request.RotationId,
        request.FencingToken,
        memberId,
        now,
        cancellationToken);

    internal static async Task<VaultKeyRotation> LoadAsync(
        VaultDomainReadContext context,
        Guid organizationId,
        Guid vaultId,
        Guid rotationId,
        Guid fencingToken,
        Guid memberId,
        Instant now,
        CancellationToken cancellationToken)
    {
        var rotation = await context.VaultKeyRotations.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId
            && x.VaultId == vaultId
            && x.Id == rotationId,
            cancellationToken);
        if (rotation is null)
        {
            throw new EntityNotFoundException(typeof(VaultKeyRotation), rotationId.ToString());
        }
        rotation.AssertLease(memberId, fencingToken, now);
        return rotation;
    }
}
