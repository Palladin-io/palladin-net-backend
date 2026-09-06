using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record GetAgentPairingStatusRequest
{
    public Guid PairingId { get; init; }
}

[PublicAPI]
public sealed record AgentPairingCredentialEnvelopeResponse(
    string Suite,
    string EphemeralPublicKey,
    string Nonce,
    string Ciphertext);

[PublicAPI]
public sealed record GetAgentPairingStatusResponse(
    string Status,
    Guid? OrganizationId,
    Guid? AgentId,
    Guid? ApiKeyId,
    AgentPairingCredentialEnvelopeResponse? Credential,
    string? DisplayName,
    string? Type);

[PublicAPI]
internal sealed class GetAgentPairingStatusEndpoint(
    AgentsDomainReadContext domainReadContext,
    AgentSignatureVerifier signatureVerifier,
    IClock clock) : Endpoint<GetAgentPairingStatusRequest, GetAgentPairingStatusResponse>
{
    public override void Configure()
    {
        Get("api/agent-pairings/{PairingId}/status");
        AllowAnonymous();
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Poll browser pairing status";
            summary.Description = "Returns a value-free status or a credential envelope encrypted to the pairing Agent identity.";
        });
    }

    public override async Task HandleAsync(GetAgentPairingStatusRequest req, CancellationToken ct)
    {
        var pairing = await domainReadContext.AgentPairingRequests
            .FirstOrDefaultAsync(x => x.Id == req.PairingId, ct);
        if (pairing is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var signature = await signatureVerifier.VerifyAsync(HttpContext.Request, pairing.Id, pairing.SigningPublicKey, ct);
        if (signature != AgentSignatureResult.Valid)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (pairing.Status == AgentPairingStatus.Rejected)
        {
            await Send.OkAsync(new GetAgentPairingStatusResponse("rejected", null, null, null, null, null, null), ct);
            return;
        }

        if (pairing.Status == AgentPairingStatus.Expired)
        {
            await Send.OkAsync(new GetAgentPairingStatusResponse("expired", null, null, null, null, null, null), ct);
            return;
        }

        if (pairing.Status == AgentPairingStatus.Pending)
        {
            var status = pairing.IsExpired(clock.GetCurrentInstant()) ? "expired" : "pending";
            await Send.OkAsync(new GetAgentPairingStatusResponse(status, null, null, null, null, null, null), ct);
            return;
        }

        var credential = new AgentPairingCredentialEnvelopeResponse(
            pairing.CredentialSuite!,
            pairing.CredentialEphemeralPublicKey!,
            pairing.CredentialNonce!,
            pairing.CredentialCiphertext!);
        await Send.OkAsync(new GetAgentPairingStatusResponse(
            "active",
            pairing.OrganizationId,
            pairing.AgentId,
            pairing.ApiKeyId,
            credential,
            pairing.DisplayName,
            pairing.Type), ct);
    }
}
