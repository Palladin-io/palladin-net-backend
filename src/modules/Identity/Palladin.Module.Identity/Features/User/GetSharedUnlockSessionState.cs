using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetSharedUnlockSessionStateRequest
{
    public string RefreshToken { get; init; } = string.Empty;
    public Guid LinkId { get; init; }
}

[PublicAPI]
public sealed record SharedUnlockSessionStateResponse(string Action, SharedUnlockLinkResponse? Link);

[UsedImplicitly]
internal sealed class GetSharedUnlockSessionStateValidator : Validator<GetSharedUnlockSessionStateRequest>
{
    public GetSharedUnlockSessionStateValidator()
    {
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.LinkId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class GetSharedUnlockSessionStateEndpoint(GetSharedUnlockSessionStateOperation operation) : IdentityOperationEndpoint<GetSharedUnlockSessionStateRequest, SharedUnlockSessionStateResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/session-state");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Read closing actions for this client's own logical session";
            summary.Description = "Read-only POST keeps the own refresh token out of URLs. No action is unlock authority. "
                + "The response neither renews a session nor returns its source authorization or generation.";
        });
    }

    public override async Task HandleAsync(GetSharedUnlockSessionStateRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
