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
public sealed record GetCurrentMemberEntryDeltaRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string? AfterSequence { get; init; }
    public string? ContinuationCursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class GetCurrentMemberEntryDeltaValidator : Validator<GetCurrentMemberEntryDeltaRequest>
{
    public GetCurrentMemberEntryDeltaValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AfterSequence)
            .Must(value => CurrentMemberEntrySyncProtocol.TryParseCanonicalUInt64(value, out _))
            .When(x => x.ContinuationCursor is null);
        RuleFor(x => x.AfterSequence)
            .Empty()
            .When(x => x.ContinuationCursor is not null);
        RuleFor(x => x.ContinuationCursor)
            .MaximumLength(512)
            .When(x => x.ContinuationCursor is not null);
        RuleFor(x => x.PageSize!.Value)
            .InclusiveBetween(1, CurrentMemberEntrySyncProtocol.MaxPageSize)
            .When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class GetCurrentMemberEntryDeltaEndpoint(
    VaultDomainReadContext readContext,
    CurrentMemberEntrySyncCursorProtector cursorProtector,
    IClock clock)
    : Endpoint<GetCurrentMemberEntryDeltaRequest, CurrentMemberEntryDeltaResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/current-entries/sync/delta");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Read a bounded complete current Member Entry delta";
            summary.Description = "Coalesces the largest complete Member journal prefix into policy-2 current ciphertext heads or structural tombstones with stable authenticated cursors.";
        });
        Tags("Vault/Sync");
    }

    public override async Task HandleAsync(
        GetCurrentMemberEntryDeltaRequest request,
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
        var organizationAuthority = OrganizationOfflineAccessAuthority.FromAuthenticatedPrincipal(User);
        if (organizationAuthority is null)
        {
            await Send.UnauthorizedAsync(cancellationToken);
            return;
        }

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
        DeltaCursorPayload cursor;
        if (request.ContinuationCursor is null)
        {
            _ = CurrentMemberEntrySyncProtocol.TryParseCanonicalUInt64(request.AfterSequence, out var afterSequence);
            if (afterSequence > authority.MemberSequence)
            {
                await CurrentMemberEntrySyncProtocol.SendInvalidCursorAsync(this, cancellationToken);
                return;
            }

            if (afterSequence < authority.MinRetainedMemberSequence)
            {
                await SendResetAsync(authority, cancellationToken);
                return;
            }

            cursor = new DeltaCursorPayload(afterSequence, afterSequence, authority.MemberSequence);
        }
        else
        {
            var cursorResult = cursorProtector.TryUnprotectDelta(
                request.ContinuationCursor,
                cursorContext,
                out cursor);
            if (cursorResult == CurrentMemberEntrySyncCursorReadResult.StateChanged)
            {
                await SendResetAsync(authority, cancellationToken);
                return;
            }

            if (cursorResult != CurrentMemberEntrySyncCursorReadResult.Valid
                || cursor.DeltaUpperBound > authority.MemberSequence)
            {
                await CurrentMemberEntrySyncProtocol.SendInvalidCursorAsync(this, cancellationToken);
                return;
            }
        }

        if (cursor.LastSafeScannedSequence < authority.MinRetainedMemberSequence)
        {
            await SendResetAsync(authority, cancellationToken);
            return;
        }

        if (cursor.LastSafeScannedSequence == cursor.DeltaUpperBound)
        {
            await SendEmptyAsync(authority, organizationAuthority, cursor, cancellationToken);
            return;
        }

        var pageSize = request.PageSize ?? CurrentMemberEntrySyncProtocol.DefaultPageSize;
        var rows = await readContext.GetMemberSyncJournalPage(
                organizationId,
                request.VaultId,
                cursor.LastSafeScannedSequence,
                cursor.DeltaUpperBound,
                pageSize)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            await SendResetAsync(authority, cancellationToken);
            return;
        }

        var entryIds = rows.Select(row => row.EntryId).Distinct().ToList();
        var heads = await (
                from entry in readContext.Entries
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
                where entry.OrganizationId == organizationId
                      && entry.VaultId == request.VaultId
                      && entryIds.Contains(entry.Id)
                select new { Entry = entry, EntryKey = entryKey, EntryVersion = entryVersion })
            .ToDictionaryAsync(row => row.Entry.Id, cancellationToken);

        var accessContext = authority.CreateAccessContext(clock.GetCurrentInstant());
        var affectedEntries = new HashSet<Guid>();
        var items = new List<CurrentMemberEntrySyncItem>();
        var itemBytes = 0;
        CurrentMemberEntryDeltaResponse? response = null;
        ulong lastSafe = cursor.LastSafeScannedSequence;
        foreach (var row in rows)
        {
            CurrentMemberEntrySyncItem? addedItem = null;
            if (affectedEntries.Add(row.EntryId))
            {
                if (heads.TryGetValue(row.EntryId, out var head))
                {
                    if (!VaultSyncContractMapper.TryToCurrentMemberEntryHead(
                            head.Entry,
                            head.EntryKey,
                            head.EntryVersion,
                            authority,
                            out addedItem))
                    {
                        await CurrentMemberEntrySyncProtocol.SendStateChangedAsync(this, cancellationToken);
                        return;
                    }
                }
                else
                {
                    addedItem = VaultSyncContractMapper.ToCurrentMemberEntryTombstone(row.EntryId);
                }
            }

            var sequence = (ulong)row.Sequence;
            var continuation = sequence < cursor.DeltaUpperBound
                ? cursorProtector.ProtectDelta(
                    cursorContext,
                    new DeltaCursorPayload(cursor.InitialAfterSequence, sequence, cursor.DeltaUpperBound))
                : null;
            var emptyResponse = CreateResponse(
                authority,
                accessContext,
                cursor,
                sequence,
                [],
                continuation);
            var fitsPage = addedItem is null
                ? VaultSyncResponseBudget.FitsPage(emptyResponse, itemBytes, items.Count)
                : VaultSyncResponseBudget.TryAddItem(
                    emptyResponse,
                    items,
                    ref itemBytes,
                    addedItem,
                    pageSize);
            if (!fitsPage)
            {
                if (addedItem is not null)
                {
                    affectedEntries.Remove(row.EntryId);
                }

                break;
            }

            lastSafe = sequence;
            response = emptyResponse;
        }

        if (response is null)
        {
            await CurrentMemberEntrySyncProtocol.SendSizeLimitExceededAsync(this, cancellationToken);
            return;
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

        if (lastSafe < cursor.DeltaUpperBound)
        {
            response = response with
            {
                ContinuationCursor = cursorProtector.ProtectDelta(
                    cursorContext,
                    new DeltaCursorPayload(cursor.InitialAfterSequence, lastSafe, cursor.DeltaUpperBound)),
            };
        }

        response = response with { Items = items };
        await Send.OkAsync(response, cancellationToken);
    }

    private async Task SendEmptyAsync(
        CurrentMemberEntrySyncAuthority authority,
        OrganizationOfflineAccessAuthority organizationAuthority,
        DeltaCursorPayload cursor,
        CancellationToken cancellationToken)
    {
        var currentAuthority = await CurrentMemberEntrySyncAuthority.AcquireAsync(
            authority.PrincipalId,
            authority.OrganizationId,
            authority.OrganizationMembershipGeneration,
            authority.VaultId,
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

        var accessContext = authority.CreateAccessContext(clock.GetCurrentInstant());
        await Send.OkAsync(
            CreateResponse(
                authority,
                accessContext,
                cursor,
                cursor.LastSafeScannedSequence,
                [],
                null),
            cancellationToken);
    }

    private static CurrentMemberEntryDeltaResponse CreateResponse(
        CurrentMemberEntrySyncAuthority authority,
        CurrentMemberEntryAccessContext accessContext,
        DeltaCursorPayload cursor,
        ulong appliedThrough,
        IReadOnlyList<CurrentMemberEntrySyncItem> items,
        string? continuationCursor) => new(
        cursor.DeltaUpperBound.ToString(CultureInfo.InvariantCulture),
        appliedThrough.ToString(CultureInfo.InvariantCulture),
        accessContext,
        authority.MemberVaultKey,
        items,
        continuationCursor);

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
