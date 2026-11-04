using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Cryptography;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Crypto;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Module.Agents.Features;

[PublicAPI]
public sealed record RotateAgentEncryptionKeyRequest
{
    public string WrapperSuiteId { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public string KeyBindingSignature { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record AgentEncryptionKeyStatusResponse(
    string WrapperSuiteId,
    uint KeyVersion,
    string KeyFingerprint,
    ulong? PreviousKeyEnvelopeCount);

[UsedImplicitly]
internal sealed class RotateAgentEncryptionKeyValidator : Validator<RotateAgentEncryptionKeyRequest>
{
    public RotateAgentEncryptionKeyValidator()
    {
        RuleFor(x => x.WrapperSuiteId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.PublicKey).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.KeyBindingSignature).Length(86);
    }
}

[PublicAPI]
internal sealed class RotateAgentEncryptionKeyEndpoint(AgentsDomainWriteContext domainWriteContext, IClock clock)
    : Endpoint<RotateAgentEncryptionKeyRequest, AgentEncryptionKeyStatusResponse>
{
    public override void Configure()
    {
        Post("api/agent/encryption-key");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Rotate the authenticated Agent's encryption key";
            summary.Description = "Registers a new encryption public key under an active key-wrapper suite as the "
                + "next recipient key version. keyBindingSignature is the Agent's Ed25519 signature over "
                + "palladin.agent-encryption-key-rotation.v1 binding the previous and next suite fingerprints. "
                + "X-Agent-Key stays the Agent's identifier. Re-posting the current key is idempotent. "
                + "previousKeyEnvelopeCount is null here; GET api/agent/encryption-key reports it from Vault.";
        });
        Tags("Agents/Agents");
    }

    public override async Task HandleAsync(RotateAgentEncryptionKeyRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId()!.Value;
        if (!TryDecode(req.PublicKey, out var publicKey) || !TryDecode(req.KeyBindingSignature, out var signature))
        {
            await SendErrorAsync(StatusCodes.Status400BadRequest, "malformed-agent-encryption-key", ct);
            return;
        }

        var agent = await domainWriteContext.Agents.SingleOrDefaultAsync(x => x.Id == agentId, ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.IsCurrentEncryptionKey(req.WrapperSuiteId, publicKey))
        {
            await Send.OkAsync(ToResponse(agent), ct);
            return;
        }

        byte[] nextFingerprint;
        try
        {
            KeyWrapperSuites.ResolveForSeal(req.WrapperSuiteId).ValidatePublicKey(publicKey);
            nextFingerprint = AgentEncryptionKeys.Fingerprint(req.WrapperSuiteId, publicKey);
        }
        catch (DomainException)
        {
            await SendErrorAsync(StatusCodes.Status400BadRequest, "malformed-agent-encryption-key", ct);
            return;
        }

        var previousFingerprint = agent.EncryptionKeyFingerprint();
        if (!AgentEncryptionKeys.VerifyRotation(Convert.FromBase64String(agent.SigningPublicKey), agent.Id,
                previousFingerprint, req.WrapperSuiteId, nextFingerprint, signature))
        {
            await SendErrorAsync(StatusCodes.Status403Forbidden, "key-binding-signature-invalid", ct);
            return;
        }

        try
        {
            agent.RotateEncryptionKey(req.WrapperSuiteId, publicKey, previousFingerprint, signature,
                clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateConcurrencyException)
        {
            await SendErrorAsync(StatusCodes.Status409Conflict, "agent-encryption-key-conflict", ct);
            return;
        }

        await Send.OkAsync(ToResponse(agent), ct);
    }

    private Task SendErrorAsync(int statusCode, string key, CancellationToken ct)
    {
        AddError(ErrorResponses.General(key));
        return Send.ErrorsAsync(statusCode, ct);
    }

    private static AgentEncryptionKeyStatusResponse ToResponse(Domain.Agent agent) => new(
        agent.EncryptionKeySuiteId,
        agent.RecipientKeyVersion,
        WebEncoders.Base64UrlEncode(agent.EncryptionKeyFingerprint()),
        null);

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = WebEncoders.Base64UrlDecode(value);
            return WebEncoders.Base64UrlEncode(bytes) == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
