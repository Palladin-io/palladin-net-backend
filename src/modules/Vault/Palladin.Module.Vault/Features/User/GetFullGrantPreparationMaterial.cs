using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetFullGrantPreparationMaterialRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
    public Guid? AfterEntryId { get; init; }
    public int PageSize { get; init; } = 100;
}

[PublicAPI]
public sealed record FullGrantPreparationMaterialItem(
    Guid EntryId,
    string EntryRevision,
    VaultEntryKeyContract EntryKey,
    MemberSecretEnvelopeContract MemberSecret);

[PublicAPI]
public sealed record GetFullGrantPreparationMaterialResponse(
    IReadOnlyList<FullGrantPreparationMaterialItem> Items,
    Guid? NextAfterEntryId);

[UsedImplicitly]
internal sealed class GetFullGrantPreparationMaterialValidator
    : Validator<GetFullGrantPreparationMaterialRequest>
{
    public GetFullGrantPreparationMaterialValidator(IOptions<VaultCryptoOptions> cryptoOptions)
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, cryptoOptions.Value.MaxFullGrantMaterialPageSize);
    }
}

[PublicAPI]
internal sealed class GetFullGrantPreparationMaterialEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock) : Endpoint<GetFullGrantPreparationMaterialRequest, GetFullGrantPreparationMaterialResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/grants/full/preparations/{grantId:guid}/material");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Read one bounded FULL grant material page";
            summary.Description = "Returns only the current encrypted MemberSecret and wrapped Entry key required for local grant-envelope construction. Pagination is deterministic by opaque Entry id.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(GetFullGrantPreparationMaterialRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.ContentEncoding = "identity";
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var preparation = await domainReadContext.FullGrantPreparations.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId
            && x.Id == req.GrantId, ct);
        if (preparation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (preparation.CreatedBy != userId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (clock.GetCurrentInstant() >= preparation.PreparationExpiresAt)
        {
            AddError(ErrorResponses.General("full-grant-preparation-expired"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var entriesQuery = domainReadContext.Entries
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == req.VaultId
                        && x.State == EntryState.Active);
        if (req.AfterEntryId is { } afterEntryId)
        {
            entriesQuery = entriesQuery.Where(x => x.Id.CompareTo(afterEntryId) > 0);
        }

        var window = await (
                from entry in entriesQuery
                join version in domainReadContext.EntryVersions
                    on new { entry.OrganizationId, entry.VaultId, Id = entry.Id, Revision = entry.CurrentRevision }
                    equals new { version.OrganizationId, version.VaultId, Id = version.EntryId, Revision = version.Revision }
                    into versions
                from version in versions.DefaultIfEmpty()
                join key in domainReadContext.EntryKeys
                    on new { entry.OrganizationId, entry.VaultId, Id = entry.Id, KeyVersion = entry.CurrentKeyVersion }
                    equals new { key.OrganizationId, key.VaultId, Id = key.EntryId, KeyVersion = key.KeyVersion }
                    into keys
                from key in keys.DefaultIfEmpty()
                orderby entry.Id
                select new { Entry = entry, Version = version, Key = key })
            .Take(req.PageSize + 1)
            .ToListAsync(ct);
        if (window.Any(row => row.Version is null || row.Key is null))
        {
            AddError(ErrorResponses.General("full-grant-material-head-invalid"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var rows = window.Take(req.PageSize).ToArray();
        var items = new List<FullGrantPreparationMaterialItem>(rows.Length);
        var itemBytes = 0;
        foreach (var row in rows)
        {
            var item = new FullGrantPreparationMaterialItem(
                row.Entry.Id,
                row.Entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
                VaultEnvelopeContractMapper.ToContract(row.Key!),
                VaultEnvelopeContractMapper.ToContract(row.Version!.GetMemberSecret()));
            var candidate = new GetFullGrantPreparationMaterialResponse(
                [],
                items.Count + 1 < window.Count ? row.Entry.Id : null);
            if (!VaultSyncResponseBudget.TryAddItem(
                    candidate,
                    items,
                    ref itemBytes,
                    item,
                    req.PageSize))
            {
                break;
            }
        }

        if (rows.Length > 0 && items.Count == 0)
        {
            await VaultSyncProtocol.SendSizeLimitExceededAsync(this, ct);
            return;
        }

        await Send.OkAsync(new GetFullGrantPreparationMaterialResponse(
            items,
            items.Count < window.Count ? items[^1].EntryId : null), ct);
    }
}
