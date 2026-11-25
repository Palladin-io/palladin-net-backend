using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CancelSecretKeyTransferAsSenderRequest
{
    public Guid TransferId { get; init; }
}

[PublicAPI]
internal sealed class CancelSecretKeyTransferAsSenderEndpoint(IdentityDomainWriteContext domainWriteContext)
    : Endpoint<CancelSecretKeyTransferAsSenderRequest>
{
    public override void Configure()
    {
        Delete("api/account/secret-key-transfers/{transferId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Cancel a Secret Key transfer as the sender";
            summary.Description = "Deletes the transfer, including any undelivered envelope. Only the bound sender "
                + "may cancel; other accounts receive 404.";
        });
        Tags("Identity/SecretKeyTransfers");
    }

    public override async Task HandleAsync(CancelSecretKeyTransferAsSenderRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var transfer = await domainWriteContext.SecretKeyTransfers
            .SingleOrDefaultAsync(x => x.Id == req.TransferId && x.SenderUserId == userId, ct);
        if (transfer is null)
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
