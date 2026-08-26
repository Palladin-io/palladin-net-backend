using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record VerifyWaitlistRequest
{
    public string Token { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class VerifyWaitlistValidator : Validator<VerifyWaitlistRequest>
{
    public VerifyWaitlistValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}

[PublicAPI]
internal sealed class VerifyWaitlistEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IOptions<WaitlistOptions> options,
    WaitlistDeveloperBenefitActivator waitlistDeveloperBenefitActivator,
    IClock clock) : Endpoint<VerifyWaitlistRequest>
{
    public override void Configure()
    {
        Get("api/waitlist/verify");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Verify a waitlist email";
            summary.Description =
                "Confirms a waitlist signup from the emailed link and redirects to the landing page. "
                + "Invalid or expired tokens redirect to the failure page without detail.";
        });
        Tags("Identity/Waitlist");
    }

    public override async Task HandleAsync(VerifyWaitlistRequest req, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var tokenHash = TokenService.HashToken(req.Token.Trim());
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var entry = await domainWriteContext.WaitlistEntries.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

        if (entry is null || !entry.CanVerify(now))
        {
            await Send.RedirectAsync(opts.FailedRedirectUrl, allowRemoteRedirects: true);
            return;
        }

        entry.Verify(now);
        await waitlistDeveloperBenefitActivator.TryActivateAsync(entry, now, ct);
        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.RedirectAsync(opts.VerifiedRedirectUrl, allowRemoteRedirects: true);
    }
}
