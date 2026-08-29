using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetCurrentMemberEntrySnapshotRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class GetCurrentMemberEntrySnapshotValidator
    : Validator<GetCurrentMemberEntrySnapshotRequest>
{
    public GetCurrentMemberEntrySnapshotValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.Cursor).MaximumLength(512).When(x => x.Cursor is not null);
        RuleFor(x => x.PageSize!.Value)
            .InclusiveBetween(1, CurrentMemberEntrySyncProtocol.MaxPageSize)
            .When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class GetCurrentMemberEntrySnapshotEndpoint(
    VaultDomainReadContext readContext,
    IOrganizationOfflineAccessAuthority organizationAuthority,
    CurrentMemberEntrySyncCursorProtector cursorProtector,
    IClock clock)
    : Endpoint<GetCurrentMemberEntrySnapshotRequest, CurrentMemberEntrySnapshotResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/current-entries/sync/snapshot");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Read a complete current Member Entry snapshot page";
            summary.Description = "Returns policy-2 complete current MemberIndex, MemberSecret and EntryKey heads with independently authoritative Member key and finite offline-access bindings.";
        });
        Tags("Vault/Sync");
    }

    public override async Task HandleAsync(
        GetCurrentMemberEntrySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        CurrentMemberEntrySyncProtocol.ApplyResponseHeaders(HttpContext);
        if (!CurrentMemberEntrySyncProtocol.IsSupported(HttpContext))
        {
            await CurrentMemberEntrySyncProtocol.SendUnsupportedAsync(this, cancellationToken);
            return;
        }

        var principalId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var organizationMembershipGeneration = User.GetAuthorizationVersion()!.Value;
        var authority = await CurrentMemberEntrySyncAuthority.AcquireAsync(
            principalId,
            organizationId,
            organizationMembershipGeneration,
            request.VaultId,
            organizationAuthority,
            readContext,
            cancellationToken);
        if (authority is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var cursorContext = authority.CreateCursorContext();
        ulong snapshotBaseSequence;
        Guid? lastEntryId;
        if (request.Cursor is null)
        {
            snapshotBaseSequence = authority.MemberSequence;
            lastEntryId = null;
        }
        else
        {
            var cursorResult = cursorProtector.TryUnprotectSnapshot(
                request.Cursor,
                cursorContext,
                out var cursor);
            if (cursorResult == CurrentMemberEntrySyncCursorReadResult.StateChanged)
            {
                await SendResetAsync(authority, cancellationToken);
                return;
            }

            if (cursorResult != CurrentMemberEntrySyncCursorReadResult.Valid
                || cursor.SnapshotBaseSequence > authority.MemberSequence)
            {
                await CurrentMemberEntrySyncProtocol.SendInvalidCursorAsync(this, cancellationToken);
                return;
            }

            snapshotBaseSequence = cursor.SnapshotBaseSequence;
            lastEntryId = cursor.LastEntryId;
        }

        if (snapshotBaseSequence < authority.MinRetainedMemberSequence)
        {
            await SendResetAsync(authority, cancellationToken);
            return;
        }

        var pageSize = request.PageSize ?? CurrentMemberEntrySyncProtocol.DefaultPageSize;
        var entryQuery = readContext.Entries
            .Where(entry => entry.OrganizationId == organizationId && entry.VaultId == request.VaultId);
        if (lastEntryId is not null)
        {
            entryQuery = entryQuery.Where(entry => entry.Id.CompareTo(lastEntryId.Value) > 0);
        }

        var rows = await (
                from entry in entryQuery
                join entryKey in readContext.EntryKeys
                    on new
                    {
                        entry.OrganizationId,
                        entry.VaultId,
                        EntryId = entry.Id,
                        KeyVersion = entry.CurrentKeyVersion,
                    }
                    equals new
                    {
                        entryKey.OrganizationId,
                        entryKey.VaultId,
                        entryKey.EntryId,
                        KeyVersion = entryKey.KeyVersion,
                    }
                    into currentEntryKeys
                from entryKey in currentEntryKeys.DefaultIfEmpty()
                join entryVersion in readContext.EntryVersions
                    on new
                    {
                        entry.OrganizationId,
                        entry.VaultId,
                        EntryId = entry.Id,
                        Revision = entry.CurrentRevision,
                    }
                    equals new
                    {
                        entryVersion.OrganizationId,
                        entryVersion.VaultId,
                        entryVersion.EntryId,
                        Revision = entryVersion.Revision,
                    }
                    into currentEntryVersions
                from entryVersion in currentEntryVersions.DefaultIfEmpty()
                orderby entry.Id
                select new { Entry = entry, EntryKey = entryKey, EntryVersion = entryVersion })
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var candidates = new List<CurrentMemberEntrySyncItem>(Math.Min(pageSize, rows.Count));
        foreach (var row in rows.Take(pageSize))
        {
            if (!VaultSyncContractMapper.TryToCurrentMemberEntryHead(
                    row.Entry,
                    row.EntryKey,
                    row.EntryVersion,
                    authority,
                    out var item))
            {
                await CurrentMemberEntrySyncProtocol.SendStateChangedAsync(this, cancellationToken);
                return;
            }

            candidates.Add(item);
        }

        var currentAuthority = await CurrentMemberEntrySyncAuthority.AcquireAsync(
            principalId,
            organizationId,
            organizationMembershipGeneration,
            request.VaultId,
            organizationAuthority,
            readContext,
            cancellationToken);
        if (currentAuthority is null)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        if (!authority.HasSameSecurityBinding(currentAuthority))
        {
            await SendResetAsync(currentAuthority, cancellationToken);
            return;
        }

        if (authority.MemberSequence != currentAuthority.MemberSequence)
        {
            await CurrentMemberEntrySyncProtocol.SendStateChangedAsync(this, cancellationToken);
            return;
        }

        var accessContext = authority.CreateAccessContext(clock.GetCurrentInstant());
        var included = new List<CurrentMemberEntrySyncItem>(candidates.Count);
        var itemBytes = 0;
        foreach (var candidate in candidates)
        {
            var hasMoreAfterCandidate = included.Count + 1 < rows.Count;
            var nextCursor = hasMoreAfterCandidate
                ? cursorProtector.ProtectSnapshot(
                    cursorContext,
                    new SnapshotCursorPayload(snapshotBaseSequence, candidate.EntryId))
                : null;
            var emptyResponse = new CurrentMemberEntrySnapshotResponse(
                snapshotBaseSequence.ToString(CultureInfo.InvariantCulture),
                accessContext,
                authority.MemberVaultKey,
                [],
                nextCursor);
            if (!VaultSyncResponseBudget.TryAddItem(
                    emptyResponse,
                    included,
                    ref itemBytes,
                    candidate,
                    pageSize))
            {
                break;
            }
        }

        if (candidates.Count > 0 && included.Count == 0)
        {
            await CurrentMemberEntrySyncProtocol.SendSizeLimitExceededAsync(this, cancellationToken);
            return;
        }

        var hasMore = included.Count < candidates.Count || rows.Count > pageSize;
        var response = new CurrentMemberEntrySnapshotResponse(
            snapshotBaseSequence.ToString(CultureInfo.InvariantCulture),
            accessContext,
            authority.MemberVaultKey,
            included,
            hasMore
                ? cursorProtector.ProtectSnapshot(
                    cursorContext,
                    new SnapshotCursorPayload(snapshotBaseSequence, included[^1].EntryId))
                : null);
        await Send.OkAsync(response, cancellationToken);
    }

    private Task SendResetAsync(
        CurrentMemberEntrySyncAuthority authority,
        CancellationToken cancellationToken) =>
        HttpContext.Response.SendAsync(
            new VaultSyncResetResponse(
                "resetRequired",
                authority.MemberSequence.ToString(CultureInfo.InvariantCulture),
                authority.MinRetainedMemberSequence.ToString(CultureInfo.InvariantCulture),
                true),
            StatusCodes.Status409Conflict,
            cancellation: cancellationToken);
}
