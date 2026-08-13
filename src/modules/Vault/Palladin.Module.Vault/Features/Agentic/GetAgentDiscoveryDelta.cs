using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetAgentDiscoveryDeltaRequest
{
    public Guid VaultId { get; init; }
    public string? AfterSequence { get; init; }
    public string? ContinuationCursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class GetAgentDiscoveryDeltaValidator : Validator<GetAgentDiscoveryDeltaRequest>
{
    public GetAgentDiscoveryDeltaValidator()
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
internal sealed class GetAgentDiscoveryDeltaEndpoint(
    VaultDomainReadContext readContext,
    VaultSyncCursorProtector cursorProtector)
    : Endpoint<GetAgentDiscoveryDeltaRequest, AgentDiscoveryDeltaResponse>
{
    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/discovery/sync/delta");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Read a bounded Agent Discovery ciphertext delta";
            summary.Description = "Coalesces the largest complete Discovery journal prefix into current projections or structural tombstones for the exact active Agent epoch.";
        });
        Tags("Vault/Discovery Sync");
    }

    public override async Task HandleAsync(GetAgentDiscoveryDeltaRequest req, CancellationToken ct)
    {
        VaultSyncProtocol.ApplyResponseHeaders(HttpContext);
        if (!VaultSyncProtocol.IsSupported(HttpContext))
        {
            await VaultSyncProtocol.SendUnsupportedAsync(this, ct);
            return;
        }

        var access = await AgentVaultSyncAuthorizer.AcquireAsync(User, req.VaultId, readContext, ct);
        if (access is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var context = new VaultSyncCursorContext(
            VaultSyncPrincipalType.Agent,
            access.AgentId,
            access.OrganizationId,
            req.VaultId,
            VaultSyncAudience.Discovery,
            access.Vault.CurrentVdkVersion.Value);
        DeltaCursorPayload cursor;
        if (req.ContinuationCursor is null)
        {
            var afterSequence = VaultEnvelopeContractMapper.ParseUInt64(req.AfterSequence!);
            if (afterSequence > access.Vault.DiscoverySequence.Value)
            {
                await VaultSyncProtocol.SendInvalidCursorAsync(this, ct);
                return;
            }

            cursor = new DeltaCursorPayload(afterSequence, afterSequence, access.Vault.DiscoverySequence.Value);
        }
        else if (!cursorProtector.TryUnprotectDelta(req.ContinuationCursor, context, out cursor)
                 || cursor.DeltaUpperBound > access.Vault.DiscoverySequence.Value)
        {
            await VaultSyncProtocol.SendInvalidCursorAsync(this, ct);
            return;
        }

        if (cursor.LastSafeScannedSequence < access.Vault.MinRetainedDiscoverySequence.Value)
        {
            await SendResetAsync(
                access.Vault.DiscoverySequence.Value,
                access.Vault.MinRetainedDiscoverySequence.Value,
                ct);
            return;
        }

        if (cursor.LastSafeScannedSequence == cursor.DeltaUpperBound)
        {
            if (!await AgentVaultSyncAuthorizer.IsCurrentAsync(access, readContext, ct))
            {
                await VaultSyncProtocol.SendStateChangedAsync(this, ct);
                return;
            }

            await Send.OkAsync(CreateResponse(cursor, cursor.LastSafeScannedSequence, [], null), ct);
            return;
        }

        var pageSize = req.PageSize ?? VaultSyncProtocol.DefaultPageSize;
        var rows = await readContext.SqlQuery<VaultSyncJournalRow>($"""
                SELECT "EntryId", "DiscoverySequence" AS "Sequence"
                FROM "VaultEntryVersions"
                WHERE "OrganizationId" = {access.OrganizationId}
                  AND "VaultId" = {req.VaultId}
                  AND "DiscoverySequence" > {(decimal)cursor.LastSafeScannedSequence}
                  AND "DiscoverySequence" <= {(decimal)cursor.DeltaUpperBound}
                ORDER BY "DiscoverySequence"
                LIMIT {pageSize}
                """)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await SendResetAsync(
                access.Vault.DiscoverySequence.Value,
                access.Vault.MinRetainedDiscoverySequence.Value,
                ct);
            return;
        }

        var entryIds = rows.Select(x => x.EntryId).Distinct().ToList();
        var heads = await readContext.Entries
            .Where(x => x.OrganizationId == access.OrganizationId
                        && x.VaultId == req.VaultId
                        && entryIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var affectedSet = new HashSet<Guid>();
        var items = new List<AgentDiscoverySyncItem>();
        var itemBytes = 0;
        AgentDiscoveryDeltaResponse? response = null;
        ulong lastSafe = cursor.LastSafeScannedSequence;
        foreach (var row in rows)
        {
            AgentDiscoverySyncItem? addedItem = null;
            if (affectedSet.Add(row.EntryId))
            {
                addedItem = heads.TryGetValue(row.EntryId, out var addedHead)
                    && addedHead.State == EntryState.Active
                    && addedHead.AgentDiscoveryRevision is not null
                        ? VaultSyncContractMapper.ToDiscoveryHead(addedHead)
                        : VaultSyncContractMapper.ToDiscoveryTombstone(row.EntryId);
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

        if (!await AgentVaultSyncAuthorizer.IsCurrentAsync(access, readContext, ct))
        {
            await VaultSyncProtocol.SendStateChangedAsync(this, ct);
            return;
        }

        await Send.OkAsync(response, ct);
    }

    private static AgentDiscoveryDeltaResponse CreateResponse(
        DeltaCursorPayload cursor,
        ulong appliedThrough,
        IReadOnlyList<AgentDiscoverySyncItem> items,
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
