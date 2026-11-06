using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.Crypto;
using Palladin.Core.Json;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record StartSecretKeyTransferAsRecipientRequest
{
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] RecipientPublicKey { get; init; } = [];
}

[PublicAPI]
public sealed record StartSecretKeyTransferAsRecipientResponse(
    Guid TransferId,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] Challenge,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] PollToken,
    Instant ExpiresAt);

[UsedImplicitly]
internal sealed class StartSecretKeyTransferAsRecipientValidator : Validator<StartSecretKeyTransferAsRecipientRequest>
{
    public StartSecretKeyTransferAsRecipientValidator() =>
        RuleFor(x => x.RecipientPublicKey).Must(key => key.Length == DevicePairingProtocol.RecipientPublicKeyBytes);
}

[PublicAPI]
internal sealed class StartSecretKeyTransferAsRecipientEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    LoginRateLimiter rateLimiter,
    IClock clock)
    : SecretKeyTransferEndpoint<StartSecretKeyTransferAsRecipientRequest, StartSecretKeyTransferAsRecipientResponse>
{
    public override void Configure()
    {
        Post("api/auth/secret-key-transfers");
        // Anonymous by design: the new device cannot authenticate before it holds the Secret Key. It receives only
        // a single-use poll token; the account is chosen later by the authenticated sender who scans the QR code.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Start a Secret Key transfer on the new device (recipient shows the QR code)";
            summary.Description = "Stores the recipient's one-time X-Wing public key (1216 bytes) for at most 300 "
                + "seconds and returns the transfer ID, server challenge and a single-use poll token. Only SHA-256 of "
                + "the poll token is stored. Rate-limited per source address.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(StartSecretKeyTransferAsRecipientRequest req, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (await RejectWhenLimitedAsync(await rateLimiter.AcquireSecretKeyTransferStartAsync(ClientIp, now, ct), ct))
        {
            return;
        }

        var (transfer, pollToken) = SecretKeyTransfer.StartByRecipient(req.RecipientPublicKey, now);
        domainWriteContext.Add(transfer);
        await domainWriteContext.CommitAsync(ct);
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new StartSecretKeyTransferAsRecipientResponse(
            transfer.Id, transfer.Challenge, pollToken, transfer.ExpiresAt), ct);
    }
}
