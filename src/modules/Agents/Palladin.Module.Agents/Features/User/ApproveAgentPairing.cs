using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ApproveAgentPairingRequest
{
    public Guid PairingId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public Guid ApiKeyId { get; init; }
    public string? IconKey { get; init; }
}

[PublicAPI]
public sealed record ApproveAgentPairingResponse(Guid AgentId);

[UsedImplicitly]
internal sealed class ApproveAgentPairingValidator : Validator<ApproveAgentPairingRequest>
{
    public ApproveAgentPairingValidator()
    {
        RuleFor(x => x.IconKey).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(500).When(x => x.IconKey is not null);
        RuleFor(x => x.DisplayName)
            .Must(value => AgentMetadata.TryNormalizeRequiredDisplayName(value, out _));
        RuleFor(x => x.ApiKeyId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class ApproveAgentPairingEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    AgentPairingApproval approval) : Endpoint<ApproveAgentPairingRequest, ApproveAgentPairingResponse>
{
    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/approve");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage | Permission.ReadApiKey);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(s => { s.Summary = "Approve Agent pairing with an existing API key"; });
    }

    public override async Task HandleAsync(ApproveAgentPairingRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var apiKey = await domainWriteContext.ApiKeys.FirstOrDefaultAsync(
            x => x.Id == req.ApiKeyId && x.OrganizationId == organizationId && x.Status == ApiKeyStatus.Active, ct);
        if (apiKey is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (!apiKey.TryFenceAgentPairingApproval())
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }
        var result = await approval.ApproveAsync(req.PairingId, req.DisplayName, req.IconKey,
            organizationId.Value, userId.Value, apiKey, ct);
        if (result.AgentId is { } agentId)
        {
            await Send.OkAsync(new ApproveAgentPairingResponse(agentId), ct);
            return;
        }
        await Send.StatusCodeAsync(result.StatusCode, ct);
    }
}
