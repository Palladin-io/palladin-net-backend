using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record ClaimSecretKeyTransferRequest
{
    public Guid TransferId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] PollToken { get; init; } = [];
}

[PublicAPI]
public sealed record ClaimSecretKeyTransferResponse(
    Guid AccountId,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] KemCiphertext,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] Ciphertext);

[PublicAPI]
public sealed record ClaimSecretKeyTransferPendingResponse(Guid? AccountId, string? Email);

[UsedImplicitly]
internal sealed class ClaimSecretKeyTransferValidator : Validator<ClaimSecretKeyTransferRequest>
{
    public ClaimSecretKeyTransferValidator() =>
        RuleFor(x => x.PollToken).Must(token => token.Length == SecretKeyTransfer.PollTokenBytes);
}

[PublicAPI]
internal sealed class ClaimSecretKeyTransferEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    LoginRateLimiter rateLimiter,
    IClock clock)
    : SecretKeyTransferEndpoint<ClaimSecretKeyTransferRequest, ClaimSecretKeyTransferResponse>
{
    public override void Configure()
    {
        Post("api/auth/secret-key-transfers/{transferId:guid}/claim");
        // Anonymous by design: the single-use poll token returned to the recipient is the only authority; the
        // backend compares its SHA-256 in constant time and releases the ciphertext exactly once.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Poll for and claim the sealed Secret Key";
            summary.Description = "Returns 202 while the sender has not delivered the envelope, with "
                + "{accountId, email} of the bound sender account (both null before a sender is bound), and 200 with "
                + "the sender account ID and ciphertext exactly once; the envelope is deleted in the same commit. The "
                + "202 values are unauthenticated hints: the recipient binds the accountId into the SAS the user "
                + "compares on both devices, rejects a 200 with a different accountId and uses the e-mail only for "
                + "display and sign-in. An "
                + "unknown transfer or wrong poll token returns 404, an expired one 410 pairing-expired and a "
                + "repeated claim 410 pairing-consumed.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(ClaimSecretKeyTransferRequest req, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (await RejectWhenLimitedAsync(await rateLimiter.AcquireSecretKeyTransferPollAsync(ClientIp, now, ct), ct))
        {
            return;
        }

        var users = domainWriteContext.Users;
        var found = await domainWriteContext.SecretKeyTransfers
            .Where(x => x.Id == req.TransferId)
            .Select(x => new
            {
                Transfer = x,
                SenderEmail = users.Where(user => user.Id == x.SenderUserId).Select(user => user.Email).SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (found is null || !found.Transfer.MatchesPollToken(req.PollToken))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var transfer = found.Transfer;
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (transfer.State == SecretKeyTransferState.Claimed)
        {
            await SendTransferErrorAsync(StatusCodes.Status410Gone, "pairing-consumed", ct);
            return;
        }

        if (transfer.IsExpired(now))
        {
            await SendTransferErrorAsync(StatusCodes.Status410Gone, "pairing-expired", ct);
            return;
        }

        if (transfer.Claim() is not { } envelope)
        {
            await HttpContext.Response.SendAsync(
                new ClaimSecretKeyTransferPendingResponse(transfer.SenderUserId, found.SenderEmail),
                StatusCodes.Status202Accepted,
                cancellation: ct);
            return;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendTransferErrorAsync(StatusCodes.Status410Gone, "pairing-consumed", ct);
            return;
        }

        await Send.OkAsync(new ClaimSecretKeyTransferResponse(
            envelope.AccountId, envelope.KemCiphertext, envelope.SealedSecret), ct);
    }
}
