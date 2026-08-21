using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
[AllowNonActiveOrganizationMembership]
internal sealed class ResendVerificationEmailEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IOptions<EmailVerificationOptions> options,
    IClock clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("api/auth/verify-email/resend");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Resend the email-verification link";
            summary.Description = "Issues a fresh verification token and email for the current user. "
                + "Already-verified (incl. OAuth) accounts are a 204 no-op; otherwise 202, throttled by a cooldown.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (user.EmailVerified)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var cooldown = Duration.FromMinutes(options.Value.ResendCooldownMinutes);
        var lastIssuedAt = await domainWriteContext.VerificationTokens
            .Where(t => t.UserId == user.Id && t.Purpose == VerificationTokenPurpose.EmailVerify)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (Instant?)t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (lastIssuedAt is null || now - lastIssuedAt.Value >= cooldown)
        {
            var (token, tokenHash) = SecureToken.Generate();
            domainWriteContext.Add(VerificationToken.CreateEmailVerification(
                guidProvider.Generate(), user.Id, user.Email, user.PreferredLanguage.Code, token, tokenHash,
                Duration.FromMinutes(options.Value.TokenTtlMinutes), now));
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.StatusCodeAsync(StatusCodes.Status202Accepted, ct);
    }
}
