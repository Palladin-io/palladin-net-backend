using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetMemberSnapshotRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class GetMemberSnapshotValidator : Validator<GetMemberSnapshotRequest>
{
    public GetMemberSnapshotValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize!.Value)
            .InclusiveBetween(1, VaultSyncProtocol.MaxPageSize)
            .When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class GetMemberSnapshotEndpoint(
    VaultDomainReadContext readContext,
    VaultSyncCursorProtector cursorProtector) : Endpoint<GetMemberSnapshotRequest, MemberSnapshotResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/sync/snapshot");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Read a Member ciphertext snapshot page";
            summary.Description = "Returns a race-safe snapshot boundary and opaque MemberIndex heads ordered by raw Entry UUID.";
        });
        Tags("Vault/Sync");
    }

    public override async Task HandleAsync(GetMemberSnapshotRequest req, CancellationToken ct)
    {
        VaultSyncProtocol.ApplyResponseHeaders(HttpContext);
        if (!VaultSyncProtocol.IsSupported(HttpContext))
        {
            await VaultSyncProtocol.SendUnsupportedAsync(this, ct);
            return;
        }

        var principalId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var vault = await readContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var context = new VaultSyncCursorContext(
            VaultSyncPrincipalType.Member,
            principalId,
            organizationId,
            req.VaultId,
            VaultSyncAudience.Member,
            vault.MemberKeyGeneration.Value);

        ulong baseSequence;
        Guid? lastEntryId;
        if (req.Cursor is null)
        {
            baseSequence = vault.MemberSequence.Value;
            lastEntryId = null;
        }
        else if (cursorProtector.TryUnprotectSnapshot(req.Cursor, context, out var cursor)
                 && cursor.SnapshotBaseSequence <= vault.MemberSequence.Value)
        {
            baseSequence = cursor.SnapshotBaseSequence;
            lastEntryId = cursor.LastEntryId;
        }
        else
        {
            await VaultSyncProtocol.SendInvalidCursorAsync(this, ct);
            return;
        }

        var pageSize = req.PageSize ?? VaultSyncProtocol.DefaultPageSize;
        var query = readContext.Entries
            .Where(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId);
        if (lastEntryId is not null)
        {
            query = query.Where(x => x.Id.CompareTo(lastEntryId.Value) > 0);
        }

        var rows = await (
                from entry in query
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
                select new { Entry = entry, EntryKey = entryKey })
            .OrderBy(x => x.Entry.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var candidates = rows.Take(pageSize)
            .Select(x => VaultSyncContractMapper.ToMemberHead(
                x.Entry,
                x.EntryKey ?? throw new InvalidOperationException("Entry head has no current wrapped key.")))
            .ToList();
        var included = new List<MemberSyncItem>(candidates.Count);
        var itemBytes = 0;
        MemberSnapshotResponse? response = null;
        foreach (var item in candidates)
        {
            var hasMore = included.Count + 1 < rows.Count;
            var nextCursor = hasMore
                ? cursorProtector.ProtectSnapshot(context, new SnapshotCursorPayload(baseSequence, item.EntryId))
                : null;
            var emptyCandidate = new MemberSnapshotResponse(
                baseSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [],
                nextCursor);
            if (!VaultSyncResponseBudget.TryAddItem(
                    emptyCandidate,
                    included,
                    ref itemBytes,
                    item,
                    pageSize))
            {
                break;
            }

            response = emptyCandidate;
        }

        if (candidates.Count > 0 && included.Count == 0)
        {
            await VaultSyncProtocol.SendSizeLimitExceededAsync(this, ct);
            return;
        }

        response ??= new MemberSnapshotResponse(
            baseSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [],
            null);
        response = response with { Items = included };
        if (included.Count < rows.Count && included.Count > 0)
        {
            response = response with
            {
                NextCursor = cursorProtector.ProtectSnapshot(
                    context,
                    new SnapshotCursorPayload(baseSequence, included[^1].EntryId)),
            };
        }

        await Send.OkAsync(response, ct);
    }
}
