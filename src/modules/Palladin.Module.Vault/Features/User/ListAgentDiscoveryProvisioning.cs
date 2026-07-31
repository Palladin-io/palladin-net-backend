using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListAgentDiscoveryProvisioningRequest
{
    public Guid VaultId { get; init; }
    public Guid? AfterId { get; init; }
    public int PageSize { get; init; } = 50;
}

[UsedImplicitly]
internal sealed class ListAgentDiscoveryProvisioningValidator : Validator<ListAgentDiscoveryProvisioningRequest>
{
    public ListAgentDiscoveryProvisioningValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

[PublicAPI]
public sealed record AgentDiscoveryProvisioningItem(
    Guid AgentId,
    string? AgentName,
    string X25519PublicKey,
    string Ed25519PublicKey,
    uint RecipientKeyVersion,
    string Status,
    string? ManifestRevision);

[PublicAPI]
public sealed record ListAgentDiscoveryProvisioningResponse(
    IReadOnlyList<AgentDiscoveryProvisioningItem> Items,
    Guid? NextAfterId);

[PublicAPI]
internal sealed class ListAgentDiscoveryProvisioningEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<ListAgentDiscoveryProvisioningRequest, ListAgentDiscoveryProvisioningResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{VaultId:guid}/discovery/agents");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        this.RequirePermission(Permission.VaultManage);
        Summary(summary =>
        {
            summary.Summary = "List organization-wide Agent Discovery provisioning work";
            summary.Description = "Returns one deterministic, bounded page of active organization Agents and whether the Vault has a current signed VDK envelope. Missing envelopes are explicit pending work, never a narrower authorization policy.";
        });
        Tags("Vault/Discovery");
    }

    public override async Task HandleAsync(ListAgentDiscoveryProvisioningRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var vault = await domainReadContext.Vaults.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .Where(x => x.VaultMembers.Any(member => member.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var activeAgentsQuery = domainReadContext.Agents.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == AgentStatus.Active)
            .Where(x => !domainReadContext.VaultPrincipalDeprovisionings.Any(operation =>
                operation.OrganizationId == organizationId
                && operation.PrincipalType == VaultPrincipalType.Agent
                && operation.PrincipalId == x.Id
                && operation.Status != VaultPrincipalDeprovisioningStatus.Completed));
        if (req.AfterId is { } afterId)
        {
            activeAgentsQuery = activeAgentsQuery.Where(x => x.Id.CompareTo(afterId) > 0);
        }
        var agentWindow = await activeAgentsQuery
            .OrderBy(x => x.Id)
            .Take(req.PageSize + 1)
            .Select(x => new { x.Id, x.Name, x.PublicKey, x.SigningPublicKey, x.RecipientKeyVersion, x.AccessEpoch })
            .ToListAsync(ct);
        var hasMore = agentWindow.Count > req.PageSize;
        var activeAgents = agentWindow.Take(req.PageSize).ToArray();
        var pageAgentIds = activeAgents.Select(x => x.Id).ToArray();
        var envelopes = await domainReadContext.AgentVaultDiscoveryEnvelopes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.VaultId == vault.Id
                        && pageAgentIds.Contains(x.AgentId))
            .ToDictionaryAsync(x => x.AgentId, ct);
        var items = activeAgents.Select(agent =>
        {
            envelopes.TryGetValue(agent.Id, out var envelope);
            var isCurrent = envelope is not null
                && envelope.RevokedAt is null
                && envelope.VdkVersion == vault.CurrentVdkVersion
                && envelope.ManifestSigningKeyVersion == vault.CurrentManifestSigningKeyVersion
                && envelope.AgentMessageKeyVersion == vault.CurrentAgentMessageKeyVersion
                && envelope.RecipientAgentKeyVersion.Value == agent.RecipientKeyVersion
                && envelope.ProvisionedAccessEpoch == agent.AccessEpoch;
            return new AgentDiscoveryProvisioningItem(
                agent.Id,
                agent.Name,
                agent.PublicKey,
                agent.SigningPublicKey,
                agent.RecipientKeyVersion,
                isCurrent ? "current" : "pending",
                envelope?.ManifestRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }).ToList();

        await Send.OkAsync(new ListAgentDiscoveryProvisioningResponse(
            items,
            hasMore ? activeAgents[^1].Id : null), ct);
    }
}
