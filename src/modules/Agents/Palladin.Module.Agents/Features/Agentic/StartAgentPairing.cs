using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Pairing;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.Persistence.Configurations;
using Palladin.Module.Agents.Shared;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record StartAgentPairingRequest
{
    public Guid PairingId { get; init; }
    public string PublicKey { get; init; } = string.Empty;
    public string SigningPublicKey { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Type { get; init; }
    public string? Hostname { get; init; }
}

[PublicAPI]
public sealed record StartAgentPairingResponse(
    Guid PairingId,
    string ApprovalUrl,
    Instant ExpiresAt,
    int PollIntervalMilliseconds);

[UsedImplicitly]
internal sealed class StartAgentPairingValidator : Validator<StartAgentPairingRequest>
{
    public StartAgentPairingValidator()
    {
        RuleFor(x => x.PairingId)
            .Must(id => id != Guid.Empty && id.ToString("D")[14] == '4');
        RuleFor(x => x.PublicKey).Must(AgentPairingContract.IsX25519PublicKey);
        RuleFor(x => x.SigningPublicKey).Must(AgentPairingContract.IsEd25519PublicKey);
        RuleFor(x => x.DisplayName)
            .Must(value => AgentMetadata.TryNormalizeDisplayName(value, out _));
        RuleFor(x => x.Type)
            .Must(value => AgentMetadata.TryNormalizeType(value, out _));
        RuleFor(x => x.Hostname).Must(value => value is null
            || (value.Trim().Length <= 253 && value.Trim().All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')));
    }
}

[PublicAPI]
internal sealed class StartAgentPairingEndpoint(
    AgentsDomainWriteContext domainWriteContext,
    AgentSignatureVerifier signatureVerifier,
    AgentPairingCredentialProtector credentialProtector,
    IOptions<AgentPairingOptions> options,
    IClock clock) : Endpoint<StartAgentPairingRequest, StartAgentPairingResponse>
{
    public override void Configure()
    {
        Post("api/agent-pairings");
        AllowAnonymous();
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(64 * 1024)));
        Tags("Agents/Pairing");
        Summary(summary =>
        {
            summary.Summary = "Start browser pairing for an Agent";
            summary.Description = "Registers a short-lived public Agent identity and returns an opaque browser approval URL.";
        });
    }

    public override async Task HandleAsync(StartAgentPairingRequest req, CancellationToken ct)
    {
        _ = AgentMetadata.TryNormalizeDisplayName(req.DisplayName, out var displayName);
        _ = AgentMetadata.TryNormalizeType(req.Type, out var type);
        if (!credentialProtector.CanEstablishSharedSecret(req.PublicKey))
        {
            AddError(x => x.PublicKey, "A usable X25519 public key is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var signature = await signatureVerifier.VerifyAsync(HttpContext.Request, req.PairingId, req.SigningPublicKey, ct);
        if (signature != AgentSignatureResult.Valid)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var existingRequest = await domainWriteContext.AgentPairingRequests
            .Where(x => x.Id == req.PairingId)
            .Select(x => new
            {
                x.PublicKey,
                x.SigningPublicKey,
                x.RequestedDisplayName,
                x.RequestedType,
                x.ExpiresAt,
            })
            .FirstOrDefaultAsync(ct);
        if (existingRequest is not null)
        {
            if (existingRequest.PublicKey != req.PublicKey
                || existingRequest.SigningPublicKey != req.SigningPublicKey
                || existingRequest.RequestedDisplayName != displayName
                || existingRequest.RequestedType != type)
            {
                await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
                return;
            }

            await Send.OkAsync(BuildResponse(req.PairingId, existingRequest.ExpiresAt), ct);
            return;
        }

        var existingAgent = await domainWriteContext.Agents.AnyAsync(x => x.PublicKey == req.PublicKey, ct);
        if (existingAgent)
        {
            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        // PostgreSQL persists timestamps at microsecond precision. Return the same
        // deadline on the first response and every idempotent database replay.
        var deadline = now + Duration.FromTimeSpan(options.Value.Lifetime);
        var expiresAt = Instant.FromUnixTimeTicks(deadline.ToUnixTimeTicks() / 10 * 10);
        domainWriteContext.Add(AgentPairingRequest.Create(
            req.PairingId,
            req.PublicKey,
            req.SigningPublicKey,
            displayName,
            type,
            now,
            expiresAt,
            string.IsNullOrWhiteSpace(req.Hostname) ? null : req.Hostname.Trim(),
            AgentConnectionInfo.NormalizeIp(HttpContext.Connection.RemoteIpAddress)));
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AgentPairingRequestConfiguration.PrimaryKey,
        })
        {
            domainWriteContext.Clear();
            existingRequest = await domainWriteContext.AgentPairingRequests
                .Where(x => x.Id == req.PairingId)
                .Select(x => new
                {
                    x.PublicKey,
                    x.SigningPublicKey,
                    x.RequestedDisplayName,
                    x.RequestedType,
                    x.ExpiresAt,
                })
                .FirstOrDefaultAsync(ct);
            if (existingRequest is not null
                && existingRequest.PublicKey == req.PublicKey
                && existingRequest.SigningPublicKey == req.SigningPublicKey
                && existingRequest.RequestedDisplayName == displayName
                && existingRequest.RequestedType == type)
            {
                await Send.OkAsync(BuildResponse(req.PairingId, existingRequest.ExpiresAt), ct);
                return;
            }

            await Send.StatusCodeAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.OkAsync(BuildResponse(req.PairingId, expiresAt), ct);
    }

    private StartAgentPairingResponse BuildResponse(Guid pairingId, Instant expiresAt) =>
        new(
            pairingId,
            $"{options.Value.ApprovalUrlBase.TrimEnd('/')}/agent-pairing/{pairingId:D}",
            expiresAt,
            options.Value.PollIntervalMilliseconds);
}

internal static class AgentPairingContract
{
    internal static bool IsX25519PublicKey(string value) => AgentPublicKey.TryNormalize(value, out _);
    internal static bool IsEd25519PublicKey(string value) => AgentPublicKey.TryNormalize(value, out _);
}
