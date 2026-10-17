using System.Globalization;
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
public sealed record DeliverEntryShareResponse(
    Guid ShareId, Guid OrganizationId, Guid VaultId, Guid EntryId, string SourceRevision,
    Instant ExpiresAt, byte[] Nonce, byte[] Ciphertext)
{
    public override string ToString() => nameof(DeliverEntryShareResponse);
}

[PublicAPI]
internal sealed class DeliverEntryShareEndpoint(
    VaultDomainWriteContext context, EntryShareReceiver receiver, IClock clock)
    : Endpoint<EntryShareSessionRequest, DeliverEntryShareResponse>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions/{sessionId:guid}/delivery");
        AllowAnonymous();
        Tags("Vault/Sharing");
        Summary(s =>
        {
            s.Summary = "Receive one encrypted snapshot using a verified guest session";
            s.Description = "The session bearer, required OTP/secret gates and current source authority "
                + "authorize delivery. Receipt accounting commits before ciphertext is returned; exact session retry is idempotent.";
        });
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(EntryShareSessionRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var (share, session) = await receiver.LoadSessionAsync(req.ShareId, req.SessionId, req.SessionToken, ct);
            share.Deliver(session, clock.GetCurrentInstant());
            await context.CommitAsync(ct);
            await Send.OkAsync(new DeliverEntryShareResponse(share.Id, share.OrganizationId, share.VaultId,
                share.EntryId, share.SourceRevision.Value.ToString(CultureInfo.InvariantCulture),
                share.ExpiresAt, share.Nonce, share.Ciphertext), ct);
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
