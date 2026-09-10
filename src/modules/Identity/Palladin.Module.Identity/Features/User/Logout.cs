using Palladin.Core.Security;
using Palladin.Core.Api;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
internal sealed class LogoutEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock, RefreshTokenLineageRevoker lineageRevoker) : Endpoint<LogoutRequest>
{
    public override void Configure()
    {
        Post("api/auth/logout");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Logout";
            summary.Description = "Revokes the provided refresh-token lineage, including a concurrently rotated successor.";
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

        if (existingToken is not null
            && !await lineageRevoker.RevokeForLogoutAsync(existingToken.UserId, existingToken.Id, now, ct))
        {
            AddError(ErrorResponses.General("session-logout-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
