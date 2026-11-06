using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CancelSecretKeyTransferAsRecipientRequest
{
    public Guid TransferId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] PollToken { get; init; } = [];
}

[UsedImplicitly]
internal sealed class CancelSecretKeyTransferAsRecipientValidator : Validator<CancelSecretKeyTransferAsRecipientRequest>
{
    public CancelSecretKeyTransferAsRecipientValidator() =>
        RuleFor(x => x.PollToken).Must(token => token.Length == SecretKeyTransfer.PollTokenBytes);
}

[PublicAPI]
internal sealed class CancelSecretKeyTransferAsRecipientEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    LoginRateLimiter rateLimiter,
    IClock clock)
    : SecretKeyTransferEndpoint<CancelSecretKeyTransferAsRecipientRequest, EmptyResponse>
{
    public override void Configure()
    {
        Delete("api/auth/secret-key-transfers/{transferId:guid}");
        // Anonymous by design: the recipient's single-use poll token in the request body is the authority.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Cancel a Secret Key transfer as the recipient";
            summary.Description = "Deletes the transfer when the poll token matches; otherwise returns 404.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(CancelSecretKeyTransferAsRecipientRequest req, CancellationToken ct)
    {
        if (await RejectWhenLimitedAsync(
                await rateLimiter.AcquireSecretKeyTransferPollAsync(ClientIp, clock.GetCurrentInstant(), ct), ct))
        {
            return;
        }

        var transfer = await domainWriteContext.SecretKeyTransfers.SingleOrDefaultAsync(x => x.Id == req.TransferId, ct);
        if (transfer is null || !transfer.MatchesPollToken(req.PollToken))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        domainWriteContext.Remove(transfer);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
