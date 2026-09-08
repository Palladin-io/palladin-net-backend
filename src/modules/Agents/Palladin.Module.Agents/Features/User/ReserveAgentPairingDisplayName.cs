using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ReserveAgentPairingDisplayNameRequest
{
    public Guid PairingId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class ReserveAgentPairingDisplayNameValidator : Validator<ReserveAgentPairingDisplayNameRequest>
{
    public ReserveAgentPairingDisplayNameValidator() =>
        RuleFor(x => x.DisplayName).Must(value => AgentMetadata.TryNormalizeRequiredDisplayName(value, out _));
}

[PublicAPI]
internal sealed class ReserveAgentPairingDisplayNameEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    AgentDisplayNameCoordinator displayNameCoordinator,
    IClock clock) : Endpoint<ReserveAgentPairingDisplayNameRequest>
{
    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/display-name/reserve");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Reserve a friendly pairing display name";
            summary.Description = "Reserves one normalized candidate without revealing conflicting Agent names.";
        });
    }

    public override async Task HandleAsync(ReserveAgentPairingDisplayNameRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        _ = AgentMetadata.TryNormalizeRequiredDisplayName(req.DisplayName, out var displayName);
        var pairing = await domainWriteContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == req.PairingId, ct);
        var now = clock.GetCurrentInstant();
        if (pairing is null
            || pairing.OrganizationId != organizationId
            || pairing.Status != AgentPairingStatus.Pending
            || pairing.IsExpired(now))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await displayNameCoordinator.FenceAsync(organizationId.Value, ct);
        if (!await displayNameCoordinator.IsAvailableAsync(
                organizationId.Value, displayName, now, null, pairing.Id, ct)
            || !pairing.TryReserveDisplayName(organizationId.Value, displayName, now))
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

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
            ConstraintName: AgentPairingRequestConfiguration.ReservedNameIndex
                or AgentDisplayNameFenceConfiguration.PrimaryKey,
        })
        {
            domainWriteContext.Clear();
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
