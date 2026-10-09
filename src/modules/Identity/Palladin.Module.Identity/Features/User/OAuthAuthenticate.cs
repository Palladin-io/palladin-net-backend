using Palladin.Module.Identity.Shared;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Core.Transport;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Options;
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
public sealed record OAuthAuthenticateRequest
{
    public string Provider { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record OAuthAuthenticateResponse(
    string AccessToken,
    string RefreshToken,
    Guid UserId,
    bool IsOnboarded,
    bool EmailVerified,
    Instant? WaitlistDeveloperBenefitStartedAt,
    Instant? WaitlistDeveloperBenefitEndsAt,
    bool IsNewUser = false);

[UsedImplicitly]
internal sealed class OAuthAuthenticateValidator : Validator<OAuthAuthenticateRequest>
{
    public OAuthAuthenticateValidator()
    {
        RuleFor(x => x.Provider).NotEmpty();
        RuleFor(x => x.Token).NotEmpty();
    }
}

[PublicAPI]
internal sealed class OAuthAuthenticateEndpoint(OAuthAuthenticateOperation operation) : IdentityOperationEndpoint<OAuthAuthenticateRequest, OAuthAuthenticateResponse>
{
    public override void Configure()
    {
        Post("api/auth/oauth/{Provider}");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Authenticate via OAuth provider";
            summary.Description = "Authenticates a user via an external OAuth provider (Google, Apple, X). Creates a new account if the user does not exist.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(OAuthAuthenticateRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
