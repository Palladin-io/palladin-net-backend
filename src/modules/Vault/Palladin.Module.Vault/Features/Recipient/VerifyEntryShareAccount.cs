using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class VerifyEntryShareAccountEndpoint(
    VaultDomainWriteContext context, EntryShareReceiver receiver, EntryShareSecurity security,
    IClock clock) : Endpoint<EntryShareSessionRequest>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions/{sessionId:guid}/verify-account");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "Verify the recipient using the authenticated account's verified email");
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(EntryShareSessionRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            // This proof must remain mandatory even if the general verification rollout gate is disabled.
            if (!User.GetEmailVerified() || User.GetEmail() is not { Length: > 0 } email)
            {
                throw new EntryShareUnavailableException();
            }

            var (share, session) = await receiver.LoadSessionAsync(req.ShareId, req.SessionId, req.SessionToken, ct);
            if (share.RecipientMode != EntryShareRecipientMode.NamedRecipient || share.ProtectedRecipientEmail is null
                || security.UnprotectRecipientEmail(share.Id, share.ProtectedRecipientEmail) != CreateEntryShareEndpoint.NormalizeEmail(email))
            {
                throw new EntryShareUnavailableException();
            }

            share.VerifyAccountEmail(session, clock.GetCurrentInstant());
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
