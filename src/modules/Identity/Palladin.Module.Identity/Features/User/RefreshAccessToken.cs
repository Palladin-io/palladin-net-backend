using Palladin.Core.Guid;
using Palladin.Core.Api;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Module.Identity.Domain;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Contracts.ValueObjects;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record RefreshAccessTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record RefreshAccessTokenResponse(
    string AccessToken,
    string RefreshToken,
    Guid UserId,
    bool IsOnboarded,
    bool EmailVerified,
    Instant? WaitlistDeveloperBenefitStartedAt,
    Instant? WaitlistDeveloperBenefitEndsAt);

[UsedImplicitly]
internal sealed class RefreshAccessTokenValidator : Validator<RefreshAccessTokenRequest>
{
    public RefreshAccessTokenValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

[PublicAPI]
internal sealed class RefreshAccessTokenEndpoint(RefreshAccessTokenOperation operation)
    : Endpoint<RefreshAccessTokenRequest, RefreshAccessTokenResponse>
{
    public override void Configure()
    {
        Post("api/auth/refresh");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Refresh access token";
            summary.Description = "Issues a new access token. Rotates the refresh token only when it is close to expiry.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(RefreshAccessTokenRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await operation.ExecuteAsync(req, ct);
        if (result.Error is { } error)
        {
            AddError(error);
            await Send.ErrorsAsync(result.StatusCode, ct);
        }
        else if (result.Body is { } body)
        {
            await Send.OkAsync(body, ct);
        }
        else
        {
            await Send.StatusCodeAsync(result.StatusCode, ct);
        }
    }
}
