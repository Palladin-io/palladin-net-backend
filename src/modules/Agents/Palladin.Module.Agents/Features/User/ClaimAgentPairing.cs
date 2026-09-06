using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Shared;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record ClaimAgentPairingRequest
{
    public Guid PairingId { get; init; }
}

[PublicAPI]
public sealed record PairingApiKeyOption(Guid ApiKeyId, string Name, string KeyHint);

[PublicAPI]
public sealed record ClaimAgentPairingResponse(
    Guid PairingId,
    string? DisplayName,
    string? ReservedDisplayName,
    string? Type,
    string PublicKeyHint,
    Instant ExpiresAt,
    bool CanCreateApiKey,
    IReadOnlyList<PairingApiKeyOption> ApiKeys);

[PublicAPI]
internal sealed class ClaimAgentPairingEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentsDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ClaimAgentPairingRequest, ClaimAgentPairingResponse>
{
    public override void Configure()
    {
        Post("api/agent-pairings/{PairingId}/claim");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage | Permission.ReadApiKey);
        this.RequireEmailVerified();
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Claim an Agent pairing request";
            summary.Description = "Binds the opaque request to the authenticated organization and returns safe approval metadata.";
        });
    }

    public override async Task HandleAsync(ClaimAgentPairingRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var pairing = await domainWriteContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == req.PairingId, ct);
        var now = clock.GetCurrentInstant();
        if (pairing is null || !pairing.TryClaim(organizationId.Value, now))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            pairing = await domainWriteContext.AgentPairingRequests
                .FirstOrDefaultAsync(x => x.Id == req.PairingId, ct);
            if (pairing is null
                || pairing.OrganizationId != organizationId
                || pairing.Status != AgentPairingStatus.Pending
                || pairing.IsExpired(now))
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }
        var apiKeys = await domainReadContext.ApiKeys
            .Where(x => x.OrganizationId == organizationId && x.Status == ApiKeyStatus.Active)
            .OrderBy(x => x.Name)
            .Select(x => new PairingApiKeyOption(x.Id, x.Name, $"pl_••••{x.KeySuffix}"))
            .ToListAsync(ct);
        var canCreate = (User.GetPermissions() & Permission.WriteApiKey) == Permission.WriteApiKey;
        await Send.OkAsync(new ClaimAgentPairingResponse(
            pairing.Id,
            pairing.DisplayName,
            pairing.ReservedDisplayName,
            pairing.Type,
            AgentPublicKey.Hint(pairing.PublicKey),
            pairing.ExpiresAt,
            canCreate,
            apiKeys), ct);
    }
}
