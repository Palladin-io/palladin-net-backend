using Palladin.Core.Security;
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
using Palladin.Module.Identity.Domain;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class SwitchOrganizationBrowserSessionEndpoint(SwitchOrganizationOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<SwitchOrganizationRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/switch-organization");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        this.RequireEmailVerified();
        Validator<SwitchOrganizationValidator>();
        Tags("Identity/Auth");
        Summary(summary =>
        {
            summary.Summary = "Switch the active organization";
            summary.Description = "Issues a new session scoped to an organization the authenticated user belongs to.";
        });
    }

    public override async Task HandleAsync(SwitchOrganizationRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
