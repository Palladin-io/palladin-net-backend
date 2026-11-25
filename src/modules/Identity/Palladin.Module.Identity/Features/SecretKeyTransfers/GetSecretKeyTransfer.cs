using System.Text.Json.Serialization;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetSecretKeyTransferRequest
{
    public Guid TransferId { get; init; }
}

[PublicAPI]
public sealed record GetSecretKeyTransferResponse(
    Guid TransferId,
    Guid AccountId,
    SecretKeyTransferFlowContract Flow,
    SecretKeyTransferStateContract State,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] Challenge,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[]? RecipientPublicKey,
    Instant ExpiresAt);

[PublicAPI]
public enum SecretKeyTransferFlowContract
{
    RecipientShowsQr = 1,
    SenderShowsQr = 2,
}

[PublicAPI]
public enum SecretKeyTransferStateContract
{
    WaitingForRecipient = 1,
    WaitingForSender = 2,
    Bound = 3,
    Sealed = 4,
    Claimed = 5,
}

[PublicAPI]
internal sealed class GetSecretKeyTransferEndpoint(IdentityDomainWriteContext domainWriteContext, IClock clock)
    : Endpoint<GetSecretKeyTransferRequest, GetSecretKeyTransferResponse>
{
    public override void Configure()
    {
        Get("api/account/secret-key-transfers/{transferId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Read a Secret Key transfer as the sender";
            summary.Description = "Returns the challenge and the recipient's public key so the sender can compute and "
                + "compare the SAS. The first authenticated reader of a recipient-created transfer becomes its only "
                + "sender; every other account receives 404. An expired transfer returns 410 pairing-expired.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(GetSecretKeyTransferRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var transfer = await domainWriteContext.SecretKeyTransfers.SingleOrDefaultAsync(x => x.Id == req.TransferId, ct);
        if (transfer is null || !transfer.IsVisibleTo(userId)
            || !await domainWriteContext.Users.AnyAsync(user => user.Id == userId && user.IsOnboarded, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (transfer.IsExpired(clock.GetCurrentInstant()))
        {
            AddError(ErrorResponses.General("pairing-expired"));
            await Send.ErrorsAsync(StatusCodes.Status410Gone, ct);
            return;
        }

        if (transfer.SenderUserId is null)
        {
            transfer.BindSender(userId);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new GetSecretKeyTransferResponse(
            transfer.Id,
            userId,
            (SecretKeyTransferFlowContract)transfer.Flow,
            (SecretKeyTransferStateContract)transfer.State,
            transfer.Challenge,
            transfer.RecipientPublicKey,
            transfer.ExpiresAt), ct);
    }
}
