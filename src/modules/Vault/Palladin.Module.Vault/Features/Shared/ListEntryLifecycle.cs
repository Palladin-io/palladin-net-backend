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
using Palladin.Module.Vault.Infrastructure.History;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListEntryLifecycleRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[PublicAPI]
public sealed record EntryLifecycleListItem(
    Guid Id,
    EntryState State,
    string CurrentRevision,
    Instant UpdatedAt,
    Instant? ArchivedAt,
    Instant? DeletedAt,
    Instant? RetentionExpiresAt,
    MemberIndexEnvelopeContract MemberIndex);

[PublicAPI]
public sealed record ListEntryLifecycleResponse(
    IReadOnlyList<EntryLifecycleListItem> Items,
    string? NextCursor);

[UsedImplicitly]
internal sealed class ListEntryLifecycleValidator : Validator<ListEntryLifecycleRequest>
{
    public ListEntryLifecycleValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize!.Value).InclusiveBetween(1, 100).When(x => x.PageSize is not null);
    }
}

internal abstract class ListEntryLifecycleEndpointBase(
    VaultDomainReadContext domainReadContext) : Endpoint<ListEntryLifecycleRequest, ListEntryLifecycleResponse>
{
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;

    protected abstract EntryState State { get; }

    protected void ConfigureMemberList(string route, string summary, string description)
    {
        Get(route);
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(config =>
        {
            config.Summary = summary;
            config.Description = description;
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(ListEntryLifecycleRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var pageSize = Math.Min(req.PageSize ?? DefaultPageSize, MaximumPageSize);
        var cursor = InstantCursor.Decode(req.Cursor);
        var query = ApplyStateFilter(domainReadContext.Entries.Where(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId));
        if (cursor is not null)
        {
            query = query.Where(x =>
                x.UpdatedAt < cursor.Timestamp
                || (x.UpdatedAt == cursor.Timestamp && x.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = InstantCursor.Encode(last.UpdatedAt, last.Id);
            rows = rows.Take(pageSize).ToList();
        }

        await Send.OkAsync(new ListEntryLifecycleResponse(rows.Select(x => new EntryLifecycleListItem(
            x.Id,
            x.State,
            x.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
            x.UpdatedAt,
            x.ArchivedAt,
            x.DeletedAt,
            RetentionExpiresAt(x),
            VaultEnvelopeContractMapper.ToContract(x.GetMemberIndex()))).ToList(), nextCursor), ct);
    }

    protected virtual IQueryable<VaultEntry> ApplyStateFilter(IQueryable<VaultEntry> query) =>
        query.Where(x => x.State == State);

    protected virtual Instant? RetentionExpiresAt(VaultEntry entry) => null;
}

[PublicAPI]
internal sealed class ListArchivedEntriesEndpoint(VaultDomainReadContext domainReadContext)
    : ListEntryLifecycleEndpointBase(domainReadContext)
{
    protected override EntryState State => EntryState.Archived;

    public override void Configure() => ConfigureMemberList(
        "api/vaults/{vaultId:guid}/entries/archive",
        "List Archived encrypted Entries",
        "Returns only Archived structural heads and MemberIndex ciphertext for local presentation after unlock.");
}

[PublicAPI]
internal sealed class ListRecentlyDeletedEntriesEndpoint(
    VaultDomainReadContext domainReadContext,
    IOptions<VaultEntryLifecycleOptions> lifecycleOptions,
    IClock clock)
    : ListEntryLifecycleEndpointBase(domainReadContext)
{
    protected override EntryState State => EntryState.Deleted;

    protected override IQueryable<VaultEntry> ApplyStateFilter(IQueryable<VaultEntry> query)
    {
        var cutoff = clock.GetCurrentInstant()
                     - Duration.FromDays(lifecycleOptions.Value.RecentlyDeletedDays);
        return query.Where(x => x.State == EntryState.Deleted
                                && x.DeletedAt != null
                                && x.DeletedAt > cutoff);
    }

    protected override Instant? RetentionExpiresAt(VaultEntry entry) =>
        entry.DeletedAt + Duration.FromDays(lifecycleOptions.Value.RecentlyDeletedDays);

    public override void Configure() => ConfigureMemberList(
        "api/vaults/{vaultId:guid}/entries/recently-deleted",
        "List Recently Deleted encrypted Entries",
        "Returns recoverable Deleted structural heads and MemberIndex ciphertext during the retention window.");
}
