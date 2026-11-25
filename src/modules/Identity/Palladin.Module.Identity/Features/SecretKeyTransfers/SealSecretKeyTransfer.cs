using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.Crypto;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record SealSecretKeyTransferRequest
{
    public Guid TransferId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] KemCiphertext { get; init; } = [];
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] Ciphertext { get; init; } = [];
}

[UsedImplicitly]
internal sealed class SealSecretKeyTransferValidator : Validator<SealSecretKeyTransferRequest>
{
    public SealSecretKeyTransferValidator()
    {
        RuleFor(x => x.KemCiphertext).Must(value => value.Length == DevicePairingProtocol.KemCiphertextBytes);
        RuleFor(x => x.Ciphertext).Must(value => value.Length == SecretKeyTransfer.SealedSecretBytes);
    }
}

[PublicAPI]
internal sealed class SealSecretKeyTransferEndpoint(IdentityDomainWriteContext domainWriteContext, IClock clock)
    : SecretKeyTransferEndpoint<SealSecretKeyTransferRequest, EmptyResponse>
{
    public override void Configure()
    {
        Post("api/account/secret-key-transfers/{transferId:guid}/envelope");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Deliver the sealed Secret Key";
            summary.Description = "Stores the X-Wing ciphertext (1120 bytes) and the XChaCha20-Poly1305 ciphertext "
                + "(48 bytes) once, only for the bound sender and only after the recipient key is known. The client "
                + "seals only after the user confirmed the SAS on both devices.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(SealSecretKeyTransferRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var transfer = await domainWriteContext.SecretKeyTransfers.SingleOrDefaultAsync(x => x.Id == req.TransferId, ct);
        if (transfer is null || transfer.SenderUserId != userId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (transfer.IsExpired(clock.GetCurrentInstant()))
        {
            await SendTransferErrorAsync(StatusCodes.Status410Gone, "pairing-expired", ct);
            return;
        }

        try
        {
            transfer.Seal(userId, req.KemCiphertext, req.Ciphertext);
            await domainWriteContext.CommitAsync(ct);
        }
        catch (SecretKeyTransferStateException exception)
        {
            await SendTransferErrorAsync(StatusCodes.Status409Conflict, exception.ErrorKey, ct);
            return;
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendTransferErrorAsync(StatusCodes.Status409Conflict, "pairing-consumed", ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
