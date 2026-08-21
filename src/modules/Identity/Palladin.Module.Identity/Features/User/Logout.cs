using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record LogoutRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class LogoutValidator : Validator<LogoutRequest>
{
    public LogoutValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

[PublicAPI]
[AllowNonActiveOrganizationMembership]
internal sealed class LogoutEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<LogoutRequest>
{
    public override void Configure()
    {
        Post("api/auth/logout");
        Summary(summary =>
        {
            summary.Summary = "Logout";
            summary.Description = "Revokes the provided refresh token to log the user out.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LogoutRequest req, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var userId = User.GetUserId();

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var tokenHash = TokenService.HashToken(req.RefreshToken);

        var existingToken = await domainWriteContext.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash && rt.UserId == userId, ct);

        if (existingToken is not null && existingToken.IsActive(now))
        {
            existingToken.RevokeForLogout(now);
            domainWriteContext.Update(existingToken);
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}
