using Palladin.Core.Api;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record VerifyEmailRequest
{
    public string Token { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record VerifyEmailResponse(
    string Status,
    Guid UserId,
    Instant? WaitlistDeveloperBenefitStartedAt,
    Instant? WaitlistDeveloperBenefitEndsAt);

[UsedImplicitly]
internal sealed class VerifyEmailValidator : Validator<VerifyEmailRequest>
{
    public VerifyEmailValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}

[PublicAPI]
internal sealed class VerifyEmailEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock) : Endpoint<VerifyEmailRequest, VerifyEmailResponse>
{
    public override void Configure()
    {
        Post("api/auth/verify-email");
        // Anonymous by design: the emailed link proves control of the mailbox; the single-use hashed
        // token is the only credential needed to flip EmailVerified.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Verify an email address";
            summary.Description = "Consumes a single-use email-verification token and marks the account verified. "
                + "Expired vs invalid tokens are reported via distinct error keys.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(VerifyEmailRequest req, CancellationToken ct)
    {
        var tokenHash = SecureToken.Hash(req.Token);
        var now = clock.GetCurrentInstant();

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var token = await domainWriteContext.VerificationTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash && t.Purpose == VerificationTokenPurpose.EmailVerify, ct);

        if (token is null || token.ConsumedAt is not null)
        {
            AddError(ErrorResponses.General("verification-token-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!token.CanConsume(now))
        {
            AddError(ErrorResponses.General("verification-token-expired"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        token.Consume(now);
        token.User.MarkEmailVerified(now);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(token.User, now, ct);
        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.OkAsync(new VerifyEmailResponse(
            "verified",
            token.User.Id,
            token.User.ActiveWaitlistDeveloperBenefitStartedAt(now),
            token.User.ActiveWaitlistDeveloperBenefitEndsAt(now)), ct);
    }
}
