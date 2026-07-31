using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.History;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetEntryHistoryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public string? BeforeRevision { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record EntryHistoryItem(
    string Revision,
    string MemberSequence,
    string? DiscoverySequence,
    Instant ChangedAt,
    ActorType ChangedByType,
    Guid ChangedById,
    EntryOperation Operation,
    uint KeyVersion,
    VaultEntryKeyContract EntryKey,
    MemberSecretEnvelopeContract MemberSecret);

[PublicAPI]
public sealed record EntryHistoryPolicy(int MaximumVersions, int MaximumAgeDays);

[PublicAPI]
public sealed record GetEntryHistoryResponse(
    string CurrentRevision,
    IReadOnlyList<EntryHistoryItem> Items,
    string? NextBeforeRevision,
    EntryHistoryPolicy Policy);

[UsedImplicitly]
internal sealed class GetEntryHistoryValidator : Validator<GetEntryHistoryRequest>
{
    public GetEntryHistoryValidator(IOptions<VaultHistoryOptions> options)
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.PageSize!.Value)
            .InclusiveBetween(1, options.Value.MaximumPageSize)
            .When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class GetEntryHistoryEndpoint(
    VaultDomainReadContext domainReadContext,
    IOptions<VaultHistoryOptions> options) : Endpoint<GetEntryHistoryRequest, GetEntryHistoryResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/entries/{entryId:guid}/history");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Read immutable encrypted Entry history";
            summary.Description = "Returns bounded canonical MemberSecret versions in descending revision order. Decryption and diffing remain local to the unlocked Member client.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(GetEntryHistoryRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.ContentEncoding = "identity";
        var organizationId = User.GetOrganizationId()!.Value;
        var entry = await domainReadContext.Entries.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId
                 && x.VaultId == req.VaultId
                 && x.Id == req.EntryId,
            ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var policy = options.Value;
        var pageSize = req.PageSize ?? policy.DefaultPageSize;
        var beforeRevision = req.BeforeRevision is null
            ? ulong.MaxValue
            : VaultEnvelopeContractMapper.ParseUInt64(req.BeforeRevision);
        var rows = await domainReadContext.GetEntryHistoryPage(
                organizationId,
                req.VaultId,
                req.EntryId,
                beforeRevision,
                pageSize + 1)
            .ToListAsync(ct);
        var requiredKeyVersions = rows.Select(row => row.KeyVersion).Distinct().ToArray();
        var entryKeys = await domainReadContext.EntryKeys
            .Where(key => key.OrganizationId == organizationId
                          && key.VaultId == req.VaultId
                          && key.EntryId == req.EntryId
                          && requiredKeyVersions.Contains(key.KeyVersion))
            .ToDictionaryAsync(key => key.KeyVersion.Value, ct);

        var effectivePolicy = new EntryHistoryPolicy(policy.MaximumVersions, policy.MaximumAgeDays);
        var items = new List<EntryHistoryItem>(Math.Min(rows.Count, pageSize));
        var itemBytes = 0;
        string? nextBeforeRevision = null;
        foreach (var row in rows.Take(pageSize))
        {
            var item = new EntryHistoryItem(
                row.Revision.Value.ToString(CultureInfo.InvariantCulture),
                row.MemberSequence.Value.ToString(CultureInfo.InvariantCulture),
                row.DiscoverySequence?.Value.ToString(CultureInfo.InvariantCulture),
                row.ChangedAt,
                row.ChangedByType,
                row.ChangedById,
                row.Operation,
                row.KeyVersion.Value,
                VaultEnvelopeContractMapper.ToContract(entryKeys[row.KeyVersion.Value]),
                VaultEnvelopeContractMapper.ToContract(row.GetMemberSecret()));
            var candidate = new GetEntryHistoryResponse(
                entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
                [],
                row.Revision.Value.ToString(CultureInfo.InvariantCulture),
                effectivePolicy);
            if (!VaultSyncResponseBudget.TryAddItem(
                    candidate with { NextBeforeRevision = null },
                    items,
                    ref itemBytes,
                    item,
                    pageSize))
            {
                break;
            }
        }

        if (rows.Count > 0 && items.Count == 0)
        {
            await VaultSyncProtocol.SendSizeLimitExceededAsync(this, ct);
            return;
        }

        if (items.Count < rows.Count)
        {
            nextBeforeRevision = items[^1].Revision;
        }

        await Send.OkAsync(new GetEntryHistoryResponse(
            entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
            items,
            nextBeforeRevision,
            effectivePolicy), ct);
    }
}
