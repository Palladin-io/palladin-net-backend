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
public sealed record VerifyEntryShareOtpRequest
{
    public Guid ShareId { get; init; }
    public Guid SessionId { get; init; }
    public string SessionToken { get; init; } = string.Empty;
    public long Generation { get; init; }
    public string Code { get; init; } = string.Empty;
    public override string ToString() => nameof(VerifyEntryShareOtpRequest);
}

[UsedImplicitly]
internal sealed class VerifyEntryShareOtpValidator : Validator<VerifyEntryShareOtpRequest>
{
    public VerifyEntryShareOtpValidator()
    {
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.SessionToken).NotEmpty().Length(43);
        RuleFor(x => x.Generation).InclusiveBetween(1, int.MaxValue);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32);
    }
}

[PublicAPI]
internal sealed class VerifyEntryShareOtpEndpoint(
    VaultDomainWriteContext context, EntryShareReceiver receiver, EntryShareSecurity security,
    IOptions<EntrySharingOptions> options, IClock clock) : Endpoint<VerifyEntryShareOtpRequest>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions/{sessionId:guid}/verify-otp");
        AllowAnonymous();
        Tags("Vault/Sharing");
        Summary(s =>
        {
            s.Summary = "Verify the current one-time recipient email code";
            s.Description = "The independent guest session bearer and current code generation are required. "
                + "Failed attempts use the share-wide budget; email verification does not satisfy optional PIN/password protection.";
        });
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(VerifyEntryShareOtpRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var (share, session) = await receiver.LoadSessionAsync(req.ShareId, req.SessionId, req.SessionToken, ct);
            var now = clock.GetCurrentInstant();
            if (share.RecipientMode != EntryShareRecipientMode.NamedRecipient || session.OtpGeneration != req.Generation)
            {
                throw new EntryShareUnavailableException();
            }

            if (session.EmailVerifiedAt is not null)
            {
                await context.CommitAsync(ct);
                await Send.NoContentAsync(ct);
                return;
            }

            if (session.OtpExpiresAt is null || now >= session.OtpExpiresAt)
            {
                throw new EntryShareUnavailableException();
            }

            if (!security.VerifyOtp(session.Id, req.Code, session.OtpHash))
            {
                share.RegisterFailedAttempt(now, options.Value.FailedAttemptLimit,
                    options.Value.TotalFailedAttemptLimit, Duration.FromSeconds(options.Value.LockoutSeconds));
                await context.CommitAsync(ct);
                await Send.NotFoundAsync(ct);
                return;
            }

            share.VerifyEmail(session, now);
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
