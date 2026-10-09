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

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class AcceptOrganizationInvitationBrowserSessionEndpoint(AcceptOrganizationInvitationOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<AcceptOrganizationInvitationRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/organization/invitations/accept");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        this.RequireEmailVerified();
        Validator<AcceptOrganizationInvitationValidator>();
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
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
