using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ApproveAgentPairingWithNewKeyRequest
{
    public Guid PairingId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string NewApiKeyName { get; init; } = string.Empty;
    public string? IconKey { get; init; }
}

[UsedImplicitly]
internal sealed class ApproveAgentPairingWithNewKeyValidator : Validator<ApproveAgentPairingWithNewKeyRequest>
{
    public ApproveAgentPairingWithNewKeyValidator()
    {
        RuleFor(x => x.IconKey).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(500).When(x => x.IconKey is not null);
        RuleFor(x => x.DisplayName)
            .Must(value => AgentMetadata.TryNormalizeRequiredDisplayName(value, out _));
        RuleFor(x => x.NewApiKeyName).NotEmpty().MaximumLength(200);
    }
}

[PublicAPI]
internal sealed class ApproveAgentPairingWithNewKeyEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock,
    AgentPairingApproval approval) : Endpoint<ApproveAgentPairingWithNewKeyRequest, ApproveAgentPairingResponse>
{
    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/approve-with-new-key");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage | Permission.WriteApiKey);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(s => { s.Summary = "Approve Agent pairing with a new API key"; });
    }

    public override async Task HandleAsync(ApproveAgentPairingWithNewKeyRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var actorName = await ApiKeyActor.ResolveActorNameAsync(domainWriteContext, userId.Value, ct);
        var apiKey = ApiKey.GenerateHidden(guidProvider.Generate(), organizationId.Value,
            req.NewApiKeyName.Trim(), userId.Value, actorName, clock.GetCurrentInstant());
        domainWriteContext.Add(apiKey);
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
