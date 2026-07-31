using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ApproveAgentRequest
{
    public Guid AgentId { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? IconKey { get; init; }
    public string? IconColor { get; init; }
}

[UsedImplicitly]
internal sealed class ApproveAgentValidator : Validator<ApproveAgentRequest>
{
    public ApproveAgentValidator()
    {
        RuleFor(x => x.Name).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Type).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(50).When(x => x.Type is not null);
        RuleFor(x => x.IconKey).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(500).When(x => x.IconKey is not null);
        RuleFor(x => x.IconColor).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(20).When(x => x.IconColor is not null);
    }
}

[PublicAPI]
internal sealed class ApproveAgentEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproveAgentRequest>
{
    public override void Configure()
    {
        Post("api/agents/{AgentId}/approve");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Approve a pending agent";
            summary.Description = "Activates an agent that is awaiting approval. The agent gains access to the organization immediately.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(ApproveAgentRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainWriteContext.Agents
            .FirstOrDefaultAsync(x => x.Id == req.AgentId && x.OrganizationId == organizationId, ct);

        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Pending)
        {
            AddError("Only a pending agent can be approved.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        agent.Activate(userId.Value, clock.GetCurrentInstant(), req.Name, req.Type, req.IconKey, req.IconColor);
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
