using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

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
        RuleFor(x => x.Name).Must(x => AgentMetadata.TryNormalizeRequiredDisplayName(x, out _)).When(x => x.Name is not null);
        RuleFor(x => x.Type).Must(x => AgentMetadata.TryNormalizeType(x, out _)).When(x => x.Type is not null);
        RuleFor(x => x.IconKey).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(500).When(x => x.IconKey is not null);
        RuleFor(x => x.IconColor).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(20).When(x => x.IconColor is not null);
    }
}

[PublicAPI]
internal sealed class ApproveAgentEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    AgentDisplayNameCoordinator displayNameCoordinator,
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

        _ = AgentMetadata.TryNormalizeDisplayName(req.Name, out var name);
        _ = AgentMetadata.TryNormalizeType(req.Type, out var type);
        var now = clock.GetCurrentInstant();
        if (name is not null)
        {
            await displayNameCoordinator.FenceAsync(organizationId.Value, ct);
            if (!await displayNameCoordinator.IsAvailableAsync(
                    organizationId.Value, name, now, agent.Id, null, ct))
            {
                await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
                return;
            }
        }

        agent.Activate(userId.Value, now, name, type, req.IconKey, req.IconColor);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AgentDisplayNameFenceConfiguration.PrimaryKey,
        })
        {
            domainWriteContext.Clear();
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
