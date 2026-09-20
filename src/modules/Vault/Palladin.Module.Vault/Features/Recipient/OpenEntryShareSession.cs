using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record OpenEntryShareSessionRequest
{
    public Guid ShareId { get; init; }
    public string AccessToken { get; init; } = string.Empty;
    public override string ToString() => nameof(OpenEntryShareSessionRequest);
}

[PublicAPI]
public sealed record OpenEntryShareSessionResponse(
    Guid SessionId, string SessionToken, Instant ExpiresAt,
    EntryShareRecipientMode RecipientMode, EntryShareProtection Protection)
{
    public override string ToString() => nameof(OpenEntryShareSessionResponse);
}

[UsedImplicitly]
internal sealed class OpenEntryShareSessionValidator : Validator<OpenEntryShareSessionRequest>
{
    public OpenEntryShareSessionValidator()
    {
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.AccessToken).NotEmpty().Length(43);
    }
}

[PublicAPI]
internal sealed class OpenEntryShareSessionEndpoint(
    VaultDomainWriteContext context, EntryShareAuthority authority, EntryShareSecurity security,
    IOptions<EntrySharingOptions> options, IGuidProvider guidProvider, IClock clock)
    : Endpoint<OpenEntryShareSessionRequest, OpenEntryShareSessionResponse>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions");
        AllowAnonymous();
        Tags("Vault/Sharing");
        Summary(s =>
        {
            s.Summary = "Open a bounded recipient session without consuming a receipt";
            s.Description = "The independent link access bearer authenticates this guest exchange. "
                + "A session does not bypass email or optional-secret verification and never contains an encryption key.";
        });
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(OpenEntryShareSessionRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var share = await context.EntryShares.SingleOrDefaultAsync(x => x.Id == req.ShareId, ct);
            if (share is null || !security.VerifyAccessToken(req.ShareId, req.AccessToken, share.AccessTokenHash))
            {
                throw new EntryShareUnavailableException();
            }

            await authority.EnsureRecipientSourceAsync(share, ct);
            var now = clock.GetCurrentInstant();
            var activeSessions = await context.EntryShareSessions.CountAsync(
                x => x.ShareId == share.Id && x.ExpiresAt > now, ct);
            if (activeSessions >= options.Value.MaximumActiveSessionsPerShare)
            {
                throw new EntryShareUnavailableException();
            }

            var sessionId = guidProvider.Generate();
            var token = security.GenerateSessionToken();
            var session = share.OpenSession(sessionId, security.HashSessionToken(sessionId, token), now,
                Duration.FromSeconds(options.Value.SessionLifetimeSeconds));
            context.Add(session);
            await context.CommitAsync(ct);
            await Send.OkAsync(new OpenEntryShareSessionResponse(session.Id, token, session.ExpiresAt,
                share.RecipientMode, share.Protection), ct);
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
