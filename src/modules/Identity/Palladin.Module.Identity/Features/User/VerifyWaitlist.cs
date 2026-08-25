using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Serilog;

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
    IClock clock,
    ILogger logger) : Endpoint<VerifyWaitlistRequest>
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
        if (!opts.Enabled && !opts.BenefitEnabled)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        string redirectUrl;
        try
        {
            var tokenHash = TokenService.HashToken(req.Token.Trim());
            var entry = await domainWriteContext.WaitlistEntries.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);
            var now = clock.GetCurrentInstant();

            if (entry is null || !entry.IsVerified && !entry.CanVerify(now))
            {
                redirectUrl = opts.InvalidRedirectUrl;
            }
            else if (entry.IsVerified)
            {
                redirectUrl = opts.AlreadyVerifiedRedirectUrl;
            }
            else
            {
                entry.Verify(now);
                await domainWriteContext.CommitAsync(ct);
                redirectUrl = opts.VerifiedRedirectUrl;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            logger.Warning("Waitlist verification is temporarily unavailable");
            redirectUrl = opts.TemporaryFailureRedirectUrl;
        }

        await Send.RedirectAsync(redirectUrl, allowRemoteRedirects: true);
    }
}
