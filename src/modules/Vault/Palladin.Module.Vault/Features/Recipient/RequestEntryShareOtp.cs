using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RequestEntryShareOtpRequest
{
    public Guid ShareId { get; init; }
    public Guid SessionId { get; init; }
    public string SessionToken { get; init; } = string.Empty;
    public long Generation { get; init; } = 1;
    public string Language { get; init; } = "en";
    public override string ToString() => nameof(RequestEntryShareOtpRequest);
}

[PublicAPI]
public sealed record RequestEntryShareOtpResponse(int RetryAfterSeconds);

[UsedImplicitly]
internal sealed class RequestEntryShareOtpValidator : Validator<RequestEntryShareOtpRequest>
{
    public RequestEntryShareOtpValidator()
    {
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.SessionToken).NotEmpty().Length(43);
        RuleFor(x => x.Generation).InclusiveBetween(1, int.MaxValue);
        RuleFor(x => x.Language).Must(x => x is "en" or "pl");
    }
}

[PublicAPI]
internal sealed class RequestEntryShareOtpEndpoint(
    VaultDomainWriteContext context, EntryShareReceiver receiver, EntryShareSecurity security,
    IOptions<EntrySharingOptions> options, IClock clock) : Endpoint<RequestEntryShareOtpRequest, RequestEntryShareOtpResponse>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions/{sessionId:guid}/otp");
        AllowAnonymous();
        Tags("Vault/Sharing");
        Summary(s =>
        {
            s.Summary = "Request a verification code for the sender-selected recipient";
            s.Description = "The guest session bearer authenticates this request; the caller cannot choose the email address. "
                + "Generation starts at one, exact retries reuse the current request and resends require the next generation and share-wide cooldown.";
        });
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(RequestEntryShareOtpRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var (share, session) = await receiver.LoadSessionAsync(req.ShareId, req.SessionId, req.SessionToken, ct);
            var now = clock.GetCurrentInstant();
            if (share.RecipientMode != EntryShareRecipientMode.NamedRecipient)
            {
                throw new EntryShareUnavailableException();
            }

            if (req.Generation == session.OtpGeneration
                && (session.EmailVerifiedAt is not null || session.OtpExpiresAt > now))
            {
                await context.CommitAsync(ct);
                await SendCountdownAsync(share, ct);
                return;
            }

            if (req.Generation != session.OtpGeneration + 1 || session.EmailVerifiedAt is not null)
            {
                throw new EntryShareUnavailableException();
            }

            var code = security.GenerateOtp();
            share.IssueOtp(session, security.HashOtp(session.Id, code), now,
                Duration.FromSeconds(options.Value.OtpLifetimeSeconds),
                Duration.FromSeconds(options.Value.OtpResendCooldownSeconds),
                new EntryShareOtpDelivery(req.Generation, security.ProtectOtp(share.Id, session.Id, req.Generation, code), req.Language));
            await context.CommitAsync(ct);
            await SendCountdownAsync(share, ct);
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

    private Task SendCountdownAsync(EntryShare share, CancellationToken ct) =>
        Send.OkAsync(new RequestEntryShareOtpResponse(share.OtpRetryAfterSeconds(
            clock.GetCurrentInstant(), Duration.FromSeconds(options.Value.OtpResendCooldownSeconds))), ct);
}
