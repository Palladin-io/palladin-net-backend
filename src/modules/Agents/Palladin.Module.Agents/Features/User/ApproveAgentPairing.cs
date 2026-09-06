using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Pairing;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.Persistence.Configurations;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ApproveAgentPairingRequest
{
    public Guid PairingId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public Guid? ApiKeyId { get; init; }
    public string? NewApiKeyName { get; init; }
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
        RuleFor(x => x)
            .Must(x => x.ApiKeyId.HasValue ^ !string.IsNullOrWhiteSpace(x.NewApiKeyName));
        RuleFor(x => x.NewApiKeyName)
            .MaximumLength(200)
            .When(x => x.NewApiKeyName is not null);
    }
}

[PublicAPI]
internal sealed class ApproveAgentPairingEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    AgentDisplayNameCoordinator displayNameCoordinator,
    AgentPairingCredentialProtector credentialProtector,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<ApproveAgentPairingRequest, ApproveAgentPairingResponse>
{
    private static readonly HashSet<string> PairingConflictConstraints =
    [
        "IX_agents_public_key",
        "IX_api_key_credentials_agent_id",
        "IX_api_key_credentials_key_hash",
        AgentDisplayNameFenceConfiguration.PrimaryKey,
    ];

    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/approve");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage | Permission.ReadApiKey);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Approve an Agent browser pairing";
            summary.Description = "Atomically activates a standard Agent and binds a hidden technical credential to a logical API key.";
        });
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

        _ = AgentMetadata.TryNormalizeRequiredDisplayName(req.DisplayName, out var displayName);
        var pairing = await domainWriteContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == req.PairingId, ct);
        var now = clock.GetCurrentInstant();
        if (pairing is null
            || pairing.OrganizationId != organizationId
            || pairing.Status != AgentPairingStatus.Pending
            || pairing.IsExpired(now)
            || !AgentMetadata.TryNormalizeType(pairing.Type, out var normalizedType))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await displayNameCoordinator.FenceAsync(organizationId.Value, ct);
        if (!await displayNameCoordinator.IsAvailableAsync(
                organizationId.Value, displayName, now, null, pairing.Id, ct))
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        ApiKey apiKey;
        var agentId = guidProvider.Generate();
        if (req.ApiKeyId is { } apiKeyId)
        {
            var existingApiKey = await domainWriteContext.ApiKeys.FirstOrDefaultAsync(
                x => x.Id == apiKeyId && x.OrganizationId == organizationId && x.Status == ApiKeyStatus.Active,
                ct);
            if (existingApiKey is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            apiKey = existingApiKey;
            if (!apiKey.TryFenceAgentPairingApproval())
            {
                await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
                return;
            }
        }
        else
        {
            if ((User.GetPermissions() & Permission.WriteApiKey) != Permission.WriteApiKey)
            {
                await Send.ForbiddenAsync(ct);
                return;
            }

            var actorName = await ApiKeyActor.ResolveActorNameAsync(domainReadContext, userId.Value, ct);
            apiKey = ApiKey.GenerateHidden(
                guidProvider.Generate(),
                organizationId.Value,
                req.NewApiKeyName!.Trim(),
                userId.Value,
                actorName,
                now);
            domainWriteContext.Add(apiKey);
        }

        using var credentialSecret = GeneratedApiKeySecret.Generate();
        var credential = ApiKeyCredential.FromPlaintext(
            guidProvider.Generate(), apiKey.Id, agentId, credentialSecret.Bytes, now);
        var envelope = credentialProtector.Protect(
            pairing.Id,
            organizationId.Value,
            agentId,
            apiKey.Id,
            pairing.PublicKey,
            credentialSecret.Bytes);
        var agent = Agent.Create(
            agentId,
            organizationId.Value,
            pairing.PublicKey,
            pairing.SigningPublicKey,
            normalizedType,
            now,
            displayName,
            apiKey.Id);
        agent.Activate(userId.Value, now, displayName, normalizedType, req.IconKey, null);
        agent.RecordBrowserPairingEnrollment();
        if (!pairing.TryApprove(organizationId.Value, agentId, apiKey.Id, displayName, normalizedType, envelope, now))
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        domainWriteContext.Add(agent);
        domainWriteContext.Add(credential);
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
            ConstraintName: { } constraintName,
        } && PairingConflictConstraints.Contains(constraintName))
        {
            domainWriteContext.Clear();
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.OkAsync(new ApproveAgentPairingResponse(agent.Id), ct);
    }
}
