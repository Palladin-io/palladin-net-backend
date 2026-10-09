using Palladin.Core.Api;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record AcceptOrganizationInvitationRequest
{
    public string Token { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class AcceptOrganizationInvitationValidator : Validator<AcceptOrganizationInvitationRequest>
{
    public AcceptOrganizationInvitationValidator() => RuleFor(x => x.Token).NotEmpty();
}

[PublicAPI]
internal sealed class AcceptOrganizationInvitationEndpoint(AcceptOrganizationInvitationOperation operation) : IdentityOperationEndpoint<AcceptOrganizationInvitationRequest, AuthSessionResponse>
{
    public override void Configure()
    {
        Post("api/organization/invitations/accept");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Accept an organization invitation";
            summary.Description = "Rechecks seat capacity and consumes a single-use invitation when its email matches the authenticated account.";
        });
    }

    public override async Task HandleAsync(AcceptOrganizationInvitationRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
