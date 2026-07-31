using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetGrantStatusRequest
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
}

// Status only — no credential material. A separate delivery endpoint returns the re-encrypted secret.
[PublicAPI]
public sealed record GetGrantStatusResponse(
    Guid GrantId,
    GrantStatus Status,
    Instant? ExpiresAt,
    int? QueryLimit);

[UsedImplicitly]
internal sealed class GetGrantStatusValidator : Validator<GetGrantStatusRequest>
{
    public GetGrantStatusValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class GetGrantStatusEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<GetGrantStatusRequest, GetGrantStatusResponse>
{
    public override void Configure()
    {
        Get("api/agent/vaults/{vaultId:guid}/grants/{grantId:guid}/status");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Agent polls a grant's status";
            summary.Description = "Returns the status of the agent's own grant so it can poll for approval. Returns status and policy only — never credential material (delivery is a separate endpoint).";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(GetGrantStatusRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var grant = await domainReadContext.Grants
            .Where(g => g.Id == req.GrantId
                        && g.VaultId == req.VaultId
                        && g.AgentId == agentId.Value
                        && g.OrganizationId == organizationId.Value
                        && g.AgentAccessEpoch == accessEpoch.Value)
            .Where(g => domainReadContext.Agents.Any(agent =>
                agent.Id == agentId.Value
                && agent.OrganizationId == organizationId.Value
                && agent.Status == AgentStatus.Active
                && agent.AccessEpoch == accessEpoch.Value))
            .Select(g => new GetGrantStatusResponse(g.Id, g.Status, g.ExpiresAt, g.QueryLimit))
            .FirstOrDefaultAsync(ct);

        if (grant is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(grant, ct);
    }
}
