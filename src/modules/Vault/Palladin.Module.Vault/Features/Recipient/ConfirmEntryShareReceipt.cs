using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class ConfirmEntryShareReceiptEndpoint(
    VaultDomainWriteContext context, EntryShareReceiver receiver, IClock clock) : Endpoint<EntryShareSessionRequest>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions/{sessionId:guid}/confirmation");
        AllowAnonymous();
        Tags("Vault/Sharing");
        Summary(s =>
        {
            s.Summary = "Confirm local display of a previously delivered sharing snapshot";
            s.Description = "A currently authorized guest session must already have received the packet. "
                + "This client assertion is not proof of human reading, does not refund the receipt and is idempotent.";
        });
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(EntryShareSessionRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var (share, session) = await receiver.LoadSessionAsync(req.ShareId, req.SessionId, req.SessionToken, ct);
            share.ConfirmReceipt(session, clock.GetCurrentInstant());
            await context.CommitAsync(ct);
            await Send.NoContentAsync(ct);
        }
        catch (EntryShareUnavailableException)
        {
            await Send.NotFoundAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await Send.NotFoundAsync(ct);
        }
    }
}
