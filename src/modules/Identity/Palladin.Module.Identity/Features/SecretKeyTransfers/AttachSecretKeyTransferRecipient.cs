using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Cryptography;
using Palladin.Core.Json;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record AttachSecretKeyTransferRecipientRequest
{
    public Guid TransferId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] Challenge { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecipientPublicKey { get; init; } = [];
}

[PublicAPI]
public sealed record AttachSecretKeyTransferRecipientResponse(
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] PollToken,
    Instant ExpiresAt);

[UsedImplicitly]
internal sealed class AttachSecretKeyTransferRecipientValidator : Validator<AttachSecretKeyTransferRecipientRequest>
{
    public AttachSecretKeyTransferRecipientValidator()
    {
        RuleFor(x => x.Challenge).Must(challenge => challenge.Length == SecretKeyTransfer.ChallengeBytes);
        RuleFor(x => x.RecipientPublicKey).Must(key => key.Length == XWingKem.PublicKeyBytes);
    }
}

[PublicAPI]
internal sealed class AttachSecretKeyTransferRecipientEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    LoginRateLimiter rateLimiter,
    IClock clock)
    : SecretKeyTransferEndpoint<AttachSecretKeyTransferRecipientRequest, AttachSecretKeyTransferRecipientResponse>
{
    public override void Configure()
    {
        Put("api/auth/secret-key-transfers/{transferId:guid}/recipient");
        // Anonymous by design: the scanned challenge from the sender's QR code is the only authority, and it is
        // compared in constant time before the single-use recipient slot is filled.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Attach the new device's public key to a sender-created transfer";
            summary.Description = "Requires the challenge from the sender's QR code. Fills the single recipient slot "
                + "once and returns a single-use poll token. Unknown transfers or a wrong challenge return 404, an "
                + "expired transfer 410 pairing-expired and an already attached one 409 pairing-consumed.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(AttachSecretKeyTransferRecipientRequest req, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (await RejectWhenLimitedAsync(await rateLimiter.AcquireSecretKeyTransferStartAsync(ClientIp, now, ct), ct))
        {
            return;
        }

        var transfer = await domainWriteContext.SecretKeyTransfers
            .SingleOrDefaultAsync(x => x.Id == req.TransferId && x.Flow == SecretKeyTransferFlow.SenderShowsQr, ct);
        if (transfer is null || !transfer.MatchesChallenge(req.Challenge))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (transfer.IsExpired(now))
        {
            await SendTransferErrorAsync(StatusCodes.Status410Gone, "pairing-expired", ct);
            return;
        }

        try
        {
            var pollToken = transfer.AttachRecipient(req.RecipientPublicKey);
            await domainWriteContext.CommitAsync(ct);
            HttpContext.Response.Headers.CacheControl = "no-store";
            await Send.OkAsync(new AttachSecretKeyTransferRecipientResponse(pollToken, transfer.ExpiresAt), ct);
        }
        catch (SecretKeyTransferStateException exception)
        {
            await SendTransferErrorAsync(StatusCodes.Status409Conflict, exception.ErrorKey, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendTransferErrorAsync(StatusCodes.Status409Conflict, "pairing-consumed", ct);
        }
    }
}
