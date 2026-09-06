using Palladin.Core.Security;
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
public sealed record UpdateAgentRequest
{
    public Guid AgentId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Type { get; init; }
    public string? IconKey { get; init; }
    public string? IconColor { get; init; }
}

[UsedImplicitly]
internal sealed class UpdateAgentValidator : Validator<UpdateAgentRequest>
{
    public UpdateAgentValidator()
    {
        RuleFor(x => x.Name).Must(x => AgentMetadata.TryNormalizeRequiredDisplayName(x, out _)).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null);
        RuleFor(x => x.Type).Must(x => AgentMetadata.TryNormalizeType(x, out _)).When(x => x.Type is not null);
        RuleFor(x => x.IconKey).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(500).When(x => x.IconKey is not null);
        RuleFor(x => x.IconColor).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(20).When(x => x.IconColor is not null);
    }
}

[PublicAPI]
internal sealed class UpdateAgentEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    AgentDisplayNameCoordinator displayNameCoordinator,
    IClock clock) : Endpoint<UpdateAgentRequest>
{
    public override void Configure()
    {
        Patch("api/agents/{AgentId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Update an agent's metadata";
            summary.Description = "Updates the name, description, type, and icon of an agent. Null fields are left unchanged.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(UpdateAgentRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
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

        if (agent.Status == Palladin.Core.Types.AgentStatus.Deactivating)
        {
            AddError("Agent deactivation is in progress.");
            await Send.ErrorsAsync(409, ct);
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

        agent.Update(
            now,
            name,
            req.Description?.Trim(),
            type,
            req.IconKey,
            req.IconColor,
            updateType: req.Type is not null);
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
