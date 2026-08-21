using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetMemberDeltaRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string? AfterSequence { get; init; }
    public string? ContinuationCursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class GetMemberDeltaValidator : Validator<GetMemberDeltaRequest>
{
    public GetMemberDeltaValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AfterSequence)
            .NotEmpty()
            .When(x => x.ContinuationCursor is null);
        RuleFor(x => x.AfterSequence)
            .Empty()
            .When(x => x.ContinuationCursor is not null);
        RuleFor(x => x.PageSize!.Value)
            .InclusiveBetween(1, VaultSyncProtocol.MaxPageSize)
            .When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class GetMemberDeltaEndpoint(
    VaultDomainReadContext readContext,
    VaultSyncCursorProtector cursorProtector) : Endpoint<GetMemberDeltaRequest, MemberDeltaResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/sync/delta");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Read a bounded Member ciphertext delta";
            summary.Description = "Coalesces the largest complete Member journal prefix into current MemberIndex heads or structural tombstones.";
        });
        Tags("Vault/Sync");
    }

    public override async Task HandleAsync(GetMemberDeltaRequest req, CancellationToken ct)
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

        DeltaCursorPayload cursor;
        if (req.ContinuationCursor is null)
        {
            var afterSequence = VaultEnvelopeContractMapper.ParseUInt64(req.AfterSequence!);
            if (afterSequence > vault.MemberSequence.Value)
            {
                await VaultSyncProtocol.SendInvalidCursorAsync(this, ct);
                return;
            }

            cursor = new DeltaCursorPayload(afterSequence, afterSequence, vault.MemberSequence.Value);
        }
        else if (!cursorProtector.TryUnprotectDelta(req.ContinuationCursor, context, out cursor)
                 || cursor.DeltaUpperBound > vault.MemberSequence.Value)
        {
            await VaultSyncProtocol.SendInvalidCursorAsync(this, ct);
            return;
        }

        if (cursor.LastSafeScannedSequence < vault.MinRetainedMemberSequence.Value)
        {
            await SendResetAsync(vault.MemberSequence.Value, vault.MinRetainedMemberSequence.Value, ct);
            return;
        }

        if (cursor.LastSafeScannedSequence == cursor.DeltaUpperBound)
        {
            await Send.OkAsync(CreateResponse(cursor, cursor.LastSafeScannedSequence, [], null), ct);
            return;
        }

        var pageSize = req.PageSize ?? VaultSyncProtocol.DefaultPageSize;
        var rows = await readContext.SqlQuery<VaultSyncJournalRow>($"""
                SELECT "EntryId", "MemberSequence" AS "Sequence"
                FROM "VaultEntryVersions"
                WHERE "OrganizationId" = {organizationId}
                  AND "VaultId" = {req.VaultId}
                  AND "MemberSequence" > {(decimal)cursor.LastSafeScannedSequence}
                  AND "MemberSequence" <= {(decimal)cursor.DeltaUpperBound}
                ORDER BY "MemberSequence"
                LIMIT {pageSize}
                """)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await SendResetAsync(vault.MemberSequence.Value, vault.MinRetainedMemberSequence.Value, ct);
            return;
        }

        var entryIds = rows.Select(x => x.EntryId).Distinct().ToList();
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
                where entry.OrganizationId == organizationId
                      && entry.VaultId == req.VaultId
                      && entryIds.Contains(entry.Id)
                select new { Entry = entry, EntryKey = entryKey })
            .ToDictionaryAsync(x => x.Entry.Id, ct);
        var affectedSet = new HashSet<Guid>();
        var items = new List<MemberSyncItem>();
        var itemBytes = 0;
        MemberDeltaResponse? response = null;
        ulong lastSafe = cursor.LastSafeScannedSequence;
        foreach (var row in rows)
        {
            MemberSyncItem? addedItem = null;
            if (affectedSet.Add(row.EntryId))
            {
                addedItem = heads.TryGetValue(row.EntryId, out var addedHead)
                        ? VaultSyncContractMapper.ToMemberHead(
                            addedHead.Entry,
                            addedHead.EntryKey
                            ?? throw new InvalidOperationException("Entry head has no current wrapped key."))
                        : VaultSyncContractMapper.ToMemberTombstone(row.EntryId);
            }

            var sequence = (ulong)row.Sequence;
            var continuation = sequence < cursor.DeltaUpperBound
                ? cursorProtector.ProtectDelta(
                    context,
                    new DeltaCursorPayload(cursor.InitialAfterSequence, sequence, cursor.DeltaUpperBound))
                : null;
            var emptyCandidate = CreateResponse(cursor, sequence, [], continuation);
            var fitsPage = addedItem is null
                ? VaultSyncResponseBudget.FitsPage(emptyCandidate, itemBytes, items.Count)
                : VaultSyncResponseBudget.TryAddItem(
                    emptyCandidate,
                    items,
                    ref itemBytes,
                    addedItem,
                    pageSize);
            if (!fitsPage)
            {
                if (addedItem is not null)
                {
                    affectedSet.Remove(row.EntryId);
                }

                break;
            }

            lastSafe = sequence;
            response = emptyCandidate;
        }

        if (response is null)
        {
            await VaultSyncProtocol.SendSizeLimitExceededAsync(this, ct);
            return;
        }

        if (lastSafe < cursor.DeltaUpperBound && response.ContinuationCursor is null)
        {
            response = response with
            {
                ContinuationCursor = cursorProtector.ProtectDelta(
                    context,
                    new DeltaCursorPayload(cursor.InitialAfterSequence, lastSafe, cursor.DeltaUpperBound)),
            };
        }

        response = response with { Items = items };

        await Send.OkAsync(response, ct);
    }

    private static MemberDeltaResponse CreateResponse(
        DeltaCursorPayload cursor,
        ulong appliedThrough,
        IReadOnlyList<MemberSyncItem> items,
        string? continuation) => new(
        cursor.DeltaUpperBound.ToString(System.Globalization.CultureInfo.InvariantCulture),
        appliedThrough.ToString(System.Globalization.CultureInfo.InvariantCulture),
        items,
        continuation);

    private Task SendResetAsync(ulong current, ulong floor, CancellationToken ct) =>
        HttpContext.Response.SendAsync(
            new VaultSyncResetResponse(
                "resetRequired",
                current.ToString(System.Globalization.CultureInfo.InvariantCulture),
                floor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                true),
            409,
            cancellation: ct);
}
