using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetScriptAccessImpactRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid ScriptEntryId { get; init; }
}

[PublicAPI]
public sealed record GetScriptAccessImpactResponse(
    int EffectiveAgentCount,
    int DirectAgentCount,
    int FullAgentCount,
    bool HasOverlappingCoverage);

[UsedImplicitly]
internal sealed class GetScriptAccessImpactValidator : Validator<GetScriptAccessImpactRequest>
{
    public GetScriptAccessImpactValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.ScriptEntryId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class GetScriptAccessImpactEndpoint(
    VaultDomainReadContext domainReadContext,
    IClock clock)
    : Endpoint<GetScriptAccessImpactRequest, GetScriptAccessImpactResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/scripts/{scriptEntryId:guid}/access-impact");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Count Agents affected by a Script change";
            summary.Description = "Returns value-free unique counts for direct ScriptExecution and covering FULL Exec access. It never returns Script metadata, references, parameter names, or values.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(GetScriptAccessImpactRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var now = clock.GetCurrentInstant();
        var scriptExists = await domainReadContext.Entries.AnyAsync(entry =>
            entry.OrganizationId == organizationId
            && entry.VaultId == req.VaultId
            && entry.Id == req.ScriptEntryId
            && entry.State == EntryState.Active
            && entry.DeliveryPolicy == GrantDeliveryPolicy.ExecOnly, ct);
        if (!scriptExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var directAgentIds = await domainReadContext.Grants
            .OfType<ScriptExecutionGrant>()
            .Where(grant => grant.OrganizationId == organizationId
                && grant.VaultId == req.VaultId
                && grant.ScriptEntryId == req.ScriptEntryId
                && grant.Status == GrantStatus.Active
                && (grant.ExpiresAt == null || grant.ExpiresAt > now)
                && (grant.QueryLimit == null || grant.QueryCount < grant.QueryLimit)
                && grant.ScriptExecutionPackage != null
                && domainReadContext.Agents.Any(agent => agent.OrganizationId == organizationId
                    && agent.Id == grant.AgentId
                    && agent.Status == AgentStatus.Active
                    && agent.AccessEpoch == grant.AgentAccessEpoch))
            .Select(grant => grant.AgentId)
            .Distinct()
            .ToListAsync(ct);
        var fullAgentIds = await domainReadContext.Grants
            .OfType<FullGrant>()
            .Where(grant => grant.OrganizationId == organizationId
                && grant.VaultId == req.VaultId
                && grant.Status == GrantStatus.Active
                && (grant.ExpiresAt == null || grant.ExpiresAt > now)
                && (grant.QueryLimit == null || grant.QueryCount < grant.QueryLimit)
                && (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec
                && grant.AgentWrappedVaultKey != null
                && domainReadContext.Agents.Any(agent => agent.OrganizationId == organizationId
                    && agent.Id == grant.AgentId
                    && agent.Status == AgentStatus.Active
                    && agent.AccessEpoch == grant.AgentAccessEpoch))
            .Select(grant => grant.AgentId)
            .Distinct()
            .ToListAsync(ct);

        var overlap = directAgentIds.Intersect(fullAgentIds).Any();
        await Send.OkAsync(new GetScriptAccessImpactResponse(
            directAgentIds.Union(fullAgentIds).Count(),
            directAgentIds.Count,
            fullAgentIds.Count,
            overlap), ct);
    }
}
