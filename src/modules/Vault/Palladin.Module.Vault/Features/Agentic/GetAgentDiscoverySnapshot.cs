using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetAgentDiscoverySnapshotRequest
{
    public Guid VaultId { get; init; }
    public string? Cursor { get; init; }
    public int? PageSize { get; init; }
}

[UsedImplicitly]
internal sealed class GetAgentDiscoverySnapshotValidator : Validator<GetAgentDiscoverySnapshotRequest>
{
    public GetAgentDiscoverySnapshotValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize!.Value)
            .InclusiveBetween(1, VaultSyncProtocol.MaxPageSize)
            .When(x => x.PageSize is not null);
    }
}

[PublicAPI]
internal sealed class GetAgentDiscoverySnapshotEndpoint(
    VaultDomainReadContext readContext,
    VaultSyncCursorProtector cursorProtector)
    : Endpoint<GetAgentDiscoverySnapshotRequest, AgentDiscoverySnapshotResponse>
{
    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/discovery/sync/snapshot");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Read an Agent Discovery ciphertext snapshot page";
            summary.Description = "Returns current discoverable Entry projections only to the exact active and provisioned Agent epoch.";
        });
        Tags("Vault/Discovery Sync");
    }

    public override async Task HandleAsync(GetAgentDiscoverySnapshotRequest req, CancellationToken ct)
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
        ulong baseSequence;
        Guid? lastEntryId;
        if (req.Cursor is null)
        {
            baseSequence = access.Vault.DiscoverySequence.Value;
            lastEntryId = null;
        }
        else if (cursorProtector.TryUnprotectSnapshot(req.Cursor, context, out var cursor)
                 && cursor.SnapshotBaseSequence <= access.Vault.DiscoverySequence.Value)
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
        var query = readContext.Entries.Where(x =>
            x.OrganizationId == access.OrganizationId
            && x.VaultId == req.VaultId
            && x.State == EntryState.Active
            && x.AgentDiscoveryRevision != null);
        if (lastEntryId is not null)
        {
            query = query.Where(x => x.Id.CompareTo(lastEntryId.Value) > 0);
        }

        var rows = await query
            .OrderBy(x => x.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var candidates = rows.Take(pageSize).Select(VaultSyncContractMapper.ToDiscoveryHead).ToList();
        var included = new List<AgentDiscoverySyncItem>(candidates.Count);
        var itemBytes = 0;
        AgentDiscoverySnapshotResponse? response = null;
        foreach (var item in candidates)
        {
            var hasMore = included.Count + 1 < rows.Count;
            var nextCursor = hasMore
                ? cursorProtector.ProtectSnapshot(context, new SnapshotCursorPayload(baseSequence, item.EntryId))
                : null;
            var emptyCandidate = new AgentDiscoverySnapshotResponse(
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

        response ??= new AgentDiscoverySnapshotResponse(
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

        if (!await AgentVaultSyncAuthorizer.IsCurrentAsync(access, readContext, ct))
        {
            await VaultSyncProtocol.SendStateChangedAsync(this, ct);
            return;
        }

        await Send.OkAsync(response, ct);
    }
}
