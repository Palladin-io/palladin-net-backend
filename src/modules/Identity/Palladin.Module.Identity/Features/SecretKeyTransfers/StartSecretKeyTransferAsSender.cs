using System.Text.Json.Serialization;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record StartSecretKeyTransferAsSenderResponse(
    Guid TransferId,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] Challenge,
    Instant ExpiresAt);

[PublicAPI]
internal sealed class StartSecretKeyTransferAsSenderEndpoint(IdentityDomainWriteContext domainWriteContext, IClock clock)
    : EndpointWithoutRequest<StartSecretKeyTransferAsSenderResponse>
{
    public override void Configure()
    {
        Post("api/account/secret-key-transfers");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Start a Secret Key transfer on a signed-in device (sender shows the QR code)";
            summary.Description = "Creates a 300-second transfer bound to the authenticated account. The new device "
                + "scans the QR code and attaches its X-Wing public key with the challenge.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        if (!await domainWriteContext.Users.AnyAsync(user => user.Id == userId && user.IsOnboarded, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var transfer = SecretKeyTransfer.StartBySender(userId, clock.GetCurrentInstant());
        domainWriteContext.Add(transfer);
        await domainWriteContext.CommitAsync(ct);
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new StartSecretKeyTransferAsSenderResponse(transfer.Id, transfer.Challenge, transfer.ExpiresAt), ct);
    }
}
